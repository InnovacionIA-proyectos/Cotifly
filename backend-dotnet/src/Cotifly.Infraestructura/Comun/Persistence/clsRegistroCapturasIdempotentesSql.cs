using System.Data;
using System.Data.Common;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// <see cref="IClsRegistroCapturasIdempotentes"/> sobre la misma fila de <c>dbo_cotifly.CapturasIdempotentes</c> que reserva la
/// clave (<see cref="clsAlmacenIdempotenciaSql"/>): el folio confirmado, el <c>solicitudNexoId</c> y el <c>scorePrioridad</c> de la
/// captura sobreviven a un reinicio y los ve cualquier réplica.
/// </summary>
/// <remarks>
/// Usa la conexión y la transacción abierta del <see cref="clsCotiflyDbContext"/> del ámbito: la captura llama a
/// <see cref="RegistrarAsync"/> dentro de la transacción que confirma el folio, así el folio y su clave se confirman (o se deshacen)
/// juntos. Las demás llamadas van sin transacción (cada sentencia se confirma sola).
/// </remarks>
public sealed class clsRegistroCapturasIdempotentesSql(clsCotiflyDbContext contexto, IClsReloj reloj) : IClsRegistroCapturasIdempotentes
{
    private const string Tabla = clsAlmacenIdempotenciaSql.Tabla;
    private const string Columnas = "huella, cotizacionId, folio, solicitudNexoId, scorePrioridad, createdAt";

    private static readonly string _sqlBuscar = $"""
        SELECT TOP (1) {Columnas} FROM {Tabla}
         WHERE usuarioId = @usuarioId AND operacion = @operacion AND clave = @clave
           AND folio IS NOT NULL AND cotizacionId IS NOT NULL AND expiraAt > @ahora;
        """;

    private static readonly string _sqlBuscarPorFolio = $"""
        SELECT TOP (1) {Columnas} FROM {Tabla}
         WHERE folio = @folio AND operacion = @operacion AND cotizacionId IS NOT NULL AND expiraAt > @ahora
         ORDER BY CASE WHEN solicitudNexoId IS NULL THEN 1 ELSE 0 END, updatedAt DESC;
        """;

    // Si la fila no existe (la clave no la reservó este almacén), se crea "libre" con la captura; si otra petición la inserta a la vez,
    // se actualiza la suya.
    private static readonly string _sqlRegistrar = $"""
        SET NOCOUNT ON;
        UPDATE {Tabla}
           SET huella = @huella, cotizacionId = @cotizacionId, folio = @folio, solicitudNexoId = @solicitudNexoId,
               scorePrioridad = @scorePrioridad, updatedAt = @ahora
         WHERE usuarioId = @usuarioId AND operacion = @operacion AND clave = @clave;
        IF @@ROWCOUNT = 0
        BEGIN
            BEGIN TRY
                INSERT INTO {Tabla} (usuarioId, operacion, clave, huella, estado, cotizacionId, folio, solicitudNexoId, scorePrioridad,
                                     createdAt, updatedAt, expiraAt)
                VALUES (@usuarioId, @operacion, @clave, @huella, N'libre', @cotizacionId, @folio, @solicitudNexoId, @scorePrioridad,
                        @ahora, @ahora, @expira);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
                UPDATE {Tabla}
                   SET huella = @huella, cotizacionId = @cotizacionId, folio = @folio, solicitudNexoId = @solicitudNexoId,
                       scorePrioridad = @scorePrioridad, updatedAt = @ahora
                 WHERE usuarioId = @usuarioId AND operacion = @operacion AND clave = @clave;
            END CATCH;
        END;
        """;

    public async Task<clsCapturaIdempotente?> BuscarAsync(string ambito, string clave, CancellationToken cancellationToken = default)
    {
        var llave = clsLlaveIdempotencia.Desde(ambito, clave);
        return await LeerAsync(_sqlBuscar, comando =>
        {
            Llave(comando, llave);
            Agregar(comando, "@ahora", SqlDbType.DateTimeOffset, reloj.AhoraUtc);
        }, cancellationToken);
    }

    public Task<clsCapturaIdempotente?> BuscarPorFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        return LeerAsync(_sqlBuscarPorFolio, comando =>
        {
            Agregar(comando, "@folio", SqlDbType.NVarChar, folio, 50);
            Agregar(comando, "@operacion", SqlDbType.NVarChar, clsEnvioCosteoService.OperacionIdempotencia, 40);
            Agregar(comando, "@ahora", SqlDbType.DateTimeOffset, reloj.AhoraUtc);
        }, cancellationToken);
    }

    public async Task RegistrarAsync(string ambito, string clave, clsCapturaIdempotente captura, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captura);
        var llave = clsLlaveIdempotencia.Desde(ambito, clave);
        var ahora = reloj.AhoraUtc;
        await EjecutarAsync(_sqlRegistrar, comando =>
        {
            Llave(comando, llave);
            Agregar(comando, "@ahora", SqlDbType.DateTimeOffset, ahora);
            Agregar(comando, "@expira", SqlDbType.DateTimeOffset, ahora.Add(IClsAlmacenIdempotencia.Vigencia));
            Agregar(comando, "@huella", SqlDbType.Char, captura.Huella, 64);
            Agregar(comando, "@cotizacionId", SqlDbType.UniqueIdentifier, captura.CotizacionId);
            Agregar(comando, "@folio", SqlDbType.NVarChar, captura.Folio, 50);
            Agregar(comando, "@solicitudNexoId", SqlDbType.NVarChar, captura.SolicitudNexoId, 64);
            comando.Parameters.Add(new SqlParameter("@scorePrioridad", SqlDbType.Decimal)
            {
                Precision = 9,
                Scale = 4,
                Value = (object?)captura.ScorePrioridad ?? DBNull.Value,
            });
        }, async c => await c.ExecuteNonQueryAsync(cancellationToken), cancellationToken);
    }

    private Task<clsCapturaIdempotente?> LeerAsync(string sql, Action<DbCommand> parametros, CancellationToken cancellationToken) =>
        EjecutarAsync(sql, parametros, async comando =>
        {
            await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
            return await lector.ReadAsync(cancellationToken)
                ? new clsCapturaIdempotente(
                    lector.GetString(0),
                    lector.GetGuid(1),
                    lector.GetString(2),
                    lector.IsDBNull(3) ? null : lector.GetString(3),
                    lector.GetFieldValue<DateTimeOffset>(5),
                    lector.IsDBNull(4) ? null : lector.GetDecimal(4))
                : null;
        }, cancellationToken);

    /// <summary>Corre <paramref name="sql"/> en la conexión del <c>DbContext</c> y, si hay una abierta, en su transacción.</summary>
    private async Task<T> EjecutarAsync<T>(string sql, Action<DbCommand> parametros, Func<DbCommand, Task<T>> ejecutar, CancellationToken cancellationToken)
    {
        var baseDatos = contexto.Database;
        var abrir = baseDatos.GetDbConnection().State != ConnectionState.Open;
        if (abrir)
        {
            await baseDatos.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var comando = baseDatos.GetDbConnection().CreateCommand();
            comando.CommandText = sql;
            comando.Transaction = baseDatos.CurrentTransaction?.GetDbTransaction();
            parametros(comando);
            return await ejecutar(comando);
        }
        finally
        {
            if (abrir)
            {
                await baseDatos.CloseConnectionAsync();
            }
        }
    }

    private static void Llave(DbCommand comando, clsLlaveIdempotencia llave)
    {
        Agregar(comando, "@usuarioId", SqlDbType.UniqueIdentifier, llave.UsuarioId);
        Agregar(comando, "@operacion", SqlDbType.NVarChar, llave.Operacion, 40);
        Agregar(comando, "@clave", SqlDbType.NVarChar, llave.Clave, 64);
    }

    private static void Agregar(DbCommand comando, string nombre, SqlDbType tipo, object? valor, int largo = 0)
    {
        var parametro = new SqlParameter(nombre, tipo) { Value = valor ?? DBNull.Value };
        if (largo > 0)
        {
            parametro.Size = largo;
        }

        comando.Parameters.Add(parametro);
    }
}
