using System.Data;
using System.Security.Cryptography;
using System.Text;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>
/// <see cref="IClsCandadoSesion"/> con <c>sp_getapplock</c> (<c>Exclusive</c>, dueño <c>Session</c>) sobre una conexión propia a
/// <c>dbo_cotifly</c> que se mantiene abierta durante el turno. No usa la transacción del <c>DbContext</c>: el turno dura lo que tarde el
/// modelo y sus herramientas guardan (y el folio abre su propia transacción con candado), así que no puede ir dentro de una transacción.
/// Recurso <c>cotifly:sesion:{spaceId}:{threadId}</c> (si pasa de 255 caracteres, su SHA-256). Si la conexión se corta, SQL Server
/// libera el candado solo.
/// </summary>
public sealed class clsCandadoSesionSql(IOptions<clsOpcionesConexiones> conexiones, TimeProvider tiempo) : IClsCandadoSesion
{
    public const string Prefijo = "cotifly:sesion:";
    private const int LargoMaximoRecurso = clsAppLockSql.LargoMaximoRecurso;

    /// <summary>Margen sobre la espera para la respuesta del servidor (límite del cliente, #125).</summary>
    private static readonly TimeSpan _margenRespuesta = TimeSpan.FromSeconds(30);

    /// <summary>Espera del acuse de una cancelación antes de cerrar la conexión (#125).</summary>
    private static readonly TimeSpan _gracia = TimeSpan.FromSeconds(5);

    /// <summary><c>CommandTimeout</c> de respaldo (#125): alto, porque SqlClient lo mide con el reloj de pared.</summary>
    private const int ComandoSegundos = 120;

    public async Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var recurso = Recurso(spaceId, threadId);
        // Sin pool: el candado es de la sesión física; así nunca queda vivo en una conexión devuelta al pool.
        var conexion = new SqlConnection(new SqlConnectionStringBuilder(conexiones.Value.Cotifly ?? string.Empty) { Pooling = false }.ConnectionString);
        try
        {
            await conexion.OpenAsync(cancellationToken);

            // Límite del cliente monotónico, CommandTimeout de respaldo y cierre si no se confirma la cancelación (clsAppLockSql, #125).
            var toma = await clsAppLockSql.TomarAsync(
                conexion, transaccion: null, recurso, "Session", new clsLimitesAppLock(espera, _margenRespuesta, _gracia, ComandoSegundos), tiempo, cancellationToken);

            // 0 = concedido, 1 = concedido tras esperar; negativo = tiempo agotado, cancelado o interbloqueo.
            if (toma.Codigo >= clsAppLockSql.Concedido)
            {
                return new clsCandadoTomado(conexion, recurso, tiempo);
            }

            if (toma.ConexionCerrada)
            {
                // Ya se cerró (o se está cerrando) a la fuerza: desecharla aquí podría quedarse esperando a SqlClient.
                return null;
            }
        }
        catch
        {
            await conexion.DisposeAsync();
            throw;
        }

        await conexion.DisposeAsync();
        return null;
    }

    /// <summary>Nombre del recurso del candado (máximo 255 caracteres).</summary>
    public static string Recurso(string spaceId, string threadId)
    {
        var recurso = $"{Prefijo}{spaceId.Trim()}:{threadId.Trim()}";
        return recurso.Length <= LargoMaximoRecurso
            ? recurso
            : Prefijo + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recurso)));
    }

    private sealed class clsCandadoTomado(SqlConnection conexion, string recurso, TimeProvider tiempo) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Mismos límites que la toma (#125). Si no se confirma, cerrar la conexión también libera el candado de sesión.
            if (conexion.State == ConnectionState.Open
                && !await clsAppLockSql.LiberarAsync(
                    conexion, recurso, "Session", new clsLimitesAppLock(TimeSpan.Zero, _margenRespuesta, _gracia, ComandoSegundos), tiempo))
            {
                await clsAppLockSql.CerrarALaFuerzaAsync(conexion, _gracia, tiempo);
                return;
            }

            await conexion.DisposeAsync();
        }
    }
}
