using System.Data;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// #191: <see cref="IClsAlmacenIdempotencia"/> sobre <c>dbo_cotifly.CapturasIdempotentes</c> (<c>db/scripts/06_idempotencia_captura.sql</c>):
/// la clave sobrevive a un reinicio de la API y la comparten todas las réplicas (despliegue <c>start-first</c>, <c>replicas</c> &gt; 1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Toma</b>: un solo lote borra la fila vencida de la clave, retoma una <c>libre</c> o una <c>en_curso</c> huérfana, o inserta
/// la fila <c>en_curso</c>. El <c>UNIQUE (usuarioId, operacion, clave)</c> decide entre dos peticiones simultáneas de DOS réplicas: una
/// inserta y la otra choca, lee la fila y responde 409 <c>ESTADO_NO_PERMITE</c> con <c>detalle.motivo = CAPTURA_EN_CURSO</c> sin ejecutar
/// nada (entre réplicas no hay a quién esperar). Dentro de UNA réplica (el doble clic, el caso común) un semáforo por clave las
/// serializa como antes: la segunda espera a la primera y recibe su resultado guardado.</item>
/// <item><b>Huella</b>: la misma clave con otra huella es <c>IDEMPOTENCY_KEY_REUTILIZADA</c>, esté terminada o en curso. Una fila
/// <c>libre</c> sin folio se puede retomar con otro cuerpo (la clave no se consumió, como en memoria); con folio, no.</item>
/// <item><b>Resultado</b>: lo que <c>seGuarda</c> acepta queda en <c>resultado</c> (<see cref="clsResultadoIdempotenteJson"/>) por
/// <see cref="IClsAlmacenIdempotencia.Vigencia"/>; lo demás deja la fila <c>libre</c> (o la borra si no tiene folio) y la repetición se
/// vuelve a ejecutar.</item>
/// <item><b>Huérfanas</b>: la toma vence en <see cref="TiempoMaximoEnCurso"/>. Si la API se cae a mitad, la siguiente petición con la
/// clave la retoma pasado ese tiempo: una captura que ya tenía folio sigue por el registro (<c>clsRegistroCapturasIdempotentesSql</c>) y
/// nunca vuelve a llamar a Nexo. Cada toma lleva su id (<c>tomadaPor</c>): la petición original, si despierta tarde, ya no la pisa.</item>
/// <item><b>Vencidas</b>: se ignoran y se borran al reusar la clave; <see cref="PurgarVencidasAsync"/> (cada hora,
/// <see cref="clsLimpiezaIdempotenciaHostedService"/>) borra las demás en lotes.</item>
/// </list>
/// Usa una conexión propia (del pool) por sentencia, no la del <c>DbContext</c> de la petición: la toma y el cierre no dependen del estado
/// en que la operación deje su unidad de trabajo. Sin la tabla, <c>/health/ready</c> responde <c>falta 06</c> (no se degrada a memoria).
/// </remarks>
public sealed class clsAlmacenIdempotenciaSql(
    IOptions<clsOpcionesConexiones> conexiones, IClsReloj reloj, ILogger<clsAlmacenIdempotenciaSql> logger) : IClsAlmacenIdempotencia
{
    /// <summary>Tabla de las claves (script 06).</summary>
    public const string Tabla = "dbo_cotifly.CapturasIdempotentes";

    /// <summary>
    /// Cuánto dura la toma de una petición en curso antes de considerarla huérfana (la API se cayó a mitad). Holgado frente a la petición
    /// más larga (captura con cubicador y Nexo, con sus tiempos de espera y reintentos).
    /// </summary>
    public static readonly TimeSpan TiempoMaximoEnCurso = TimeSpan.FromMinutes(10);

    /// <summary>Filas vencidas que borra cada sentencia de <see cref="PurgarVencidasAsync"/>.</summary>
    public const int LotePurga = 1000;

    private const string EstadoEnCurso = "en_curso";
    private const string EstadoTerminada = "terminada";

    private const string Clave = "usuarioId = @usuarioId AND operacion = @operacion AND clave = @clave";

    private static readonly string _sqlTomar = $"""
        SET NOCOUNT ON;
        DELETE FROM {Tabla} WHERE {Clave} AND expiraAt <= @ahora;
        UPDATE {Tabla}
           SET estado = N'en_curso', huella = @huella, tomadaPor = @token, enCursoHasta = @hasta, resultado = NULL, updatedAt = @ahora
         WHERE {Clave}
           AND (estado = N'libre' OR (estado = N'en_curso' AND enCursoHasta <= @ahora))
           AND (folio IS NULL OR huella = @huella);
        IF @@ROWCOUNT = 0
        BEGIN
            BEGIN TRY
                INSERT INTO {Tabla} (usuarioId, operacion, clave, huella, estado, tomadaPor, enCursoHasta, createdAt, updatedAt, expiraAt)
                VALUES (@usuarioId, @operacion, @clave, @huella, N'en_curso', @token, @hasta, @ahora, @ahora, @expira);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
            END CATCH;
        END;
        SELECT estado, huella, tomadaPor, resultado, folio, solicitudNexoId FROM {Tabla} WHERE {Clave};
        """;

    private static readonly string _sqlTerminar = $"""
        UPDATE {Tabla}
           SET estado = N'terminada', resultado = @resultado, enCursoHasta = NULL, updatedAt = @ahora, expiraAt = @expira
         WHERE {Clave} AND tomadaPor = @token AND estado = N'en_curso';
        """;

    private static readonly string _sqlSoltar = $"""
        SET NOCOUNT ON;
        DELETE FROM {Tabla} WHERE {Clave} AND tomadaPor = @token AND estado = N'en_curso' AND folio IS NULL;
        UPDATE {Tabla} SET estado = N'libre', enCursoHasta = NULL, resultado = NULL, updatedAt = @ahora
         WHERE {Clave} AND tomadaPor = @token AND estado = N'en_curso';
        """;

    private static readonly string _sqlPurgar = $"DELETE TOP ({LotePurga}) FROM {Tabla} WHERE expiraAt <= @ahora;";

    private readonly object _candado = new();
    private readonly Dictionary<clsLlaveIdempotencia, clsSemaforoClave> _semaforos = [];

    public async Task<clsResultado<T>> EjecutarUnaVezAsync<T>(
        string ambito,
        string clave,
        string huella,
        Func<CancellationToken, Task<clsResultado<T>>> accion,
        Func<clsResultado<T>, bool>? seGuarda,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ambito);
        ArgumentException.ThrowIfNullOrWhiteSpace(huella);
        ArgumentNullException.ThrowIfNull(accion);
        if (string.IsNullOrWhiteSpace(clave)
            || clave.Length is < IClsAlmacenIdempotencia.LargoMinimoClave or > IClsAlmacenIdempotencia.LargoMaximoClave)
        {
            return new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                $"Idempotency-Key debe tener entre {IClsAlmacenIdempotencia.LargoMinimoClave} y {IClsAlmacenIdempotencia.LargoMaximoClave} caracteres.");
        }

        var llave = clsLlaveIdempotencia.Desde(ambito, clave);
        var semaforo = TomarSemaforo(llave);
        try
        {
            await semaforo.Semaforo.WaitAsync(ct);
            try
            {
                return await EjecutarConLaClaveAsync(llave, huella, accion, seGuarda, ct);
            }
            finally
            {
                semaforo.Semaforo.Release();
            }
        }
        finally
        {
            SoltarSemaforo(llave, semaforo);
        }
    }

    /// <summary>Borra hasta <see cref="LotePurga"/> claves vencidas; devuelve cuántas borró.</summary>
    public async Task<int> PurgarVencidasAsync(CancellationToken cancellationToken = default)
    {
        await using var conexion = await AbrirAsync(cancellationToken);
        await using var comando = new SqlCommand(_sqlPurgar, conexion);
        comando.Parameters.Add(new SqlParameter("@ahora", SqlDbType.DateTimeOffset) { Value = reloj.AhoraUtc });
        return await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<clsResultado<T>> EjecutarConLaClaveAsync<T>(
        clsLlaveIdempotencia llave,
        string huella,
        Func<CancellationToken, Task<clsResultado<T>>> accion,
        Func<clsResultado<T>, bool>? seGuarda,
        CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var fila = await TomarAsync(llave, huella, token, ct);
        if (fila.TomadaPor != token || fila.Estado != EstadoEnCurso)
        {
            return Responder<T>(fila, huella);
        }

        clsResultado<T> resultado;
        try
        {
            resultado = await accion(ct);
        }
        catch
        {
            await CerrarAsync(llave, token, null);
            throw;
        }

        await CerrarAsync(llave, token, seGuarda?.Invoke(resultado) ?? resultado.EsExitoso ? clsResultadoIdempotenteJson.Serializar(resultado) : null);
        return resultado;
    }

    /// <summary>El semáforo de la clave en esta réplica (lo crea si no existe) con un usuario más.</summary>
    private clsSemaforoClave TomarSemaforo(clsLlaveIdempotencia llave)
    {
        lock (_candado)
        {
            if (!_semaforos.TryGetValue(llave, out var semaforo))
            {
                semaforo = new clsSemaforoClave();
                _semaforos[llave] = semaforo;
            }

            semaforo.Usuarios++;
            return semaforo;
        }
    }

    /// <summary>Un usuario menos; sin usuarios, el semáforo se olvida (la clave vive en la tabla, no aquí).</summary>
    private void SoltarSemaforo(clsLlaveIdempotencia llave, clsSemaforoClave semaforo)
    {
        lock (_candado)
        {
            if (--semaforo.Usuarios == 0 && _semaforos.Remove(llave))
            {
                semaforo.Semaforo.Dispose();
            }
        }
    }

    private static clsResultado<T> Responder<T>(clsFilaIdempotencia fila, string huella)
    {
        if (!string.Equals(fila.Huella, huella, StringComparison.OrdinalIgnoreCase))
        {
            return new clsErrorDominio(clsCodigosAplicacion.IdempotencyKeyReutilizada, clsCodigosAplicacion.MensajeIdempotencyKeyReutilizada);
        }

        if (fila.Estado == EstadoTerminada && fila.Resultado is { } json)
        {
            return clsResultadoIdempotenteJson.Deserializar<T>(json);
        }

        var datos = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fila.Folio is not null)
        {
            datos["folio"] = fila.Folio;
            datos["solicitud_nexo_id"] = fila.SolicitudNexoId;
        }

        return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
            "Otra petición con esta Idempotency-Key todavía está en curso. No la repitas: espera unos segundos y revisa el resultado"
            + (fila.Folio is null ? "." : $" del folio {fila.Folio} en Mis cotizaciones."),
            clsCodigosAplicacion.MotivoCapturaEnCurso) { Datos = datos.Count == 0 ? null : datos };
    }

    private async Task<clsFilaIdempotencia> TomarAsync(clsLlaveIdempotencia llave, string huella, Guid token, CancellationToken ct)
    {
        var ahora = reloj.AhoraUtc;
        await using var conexion = await AbrirAsync(ct);
        await using var comando = new SqlCommand(_sqlTomar, conexion);
        Parametros(comando, llave, ahora);
        comando.Parameters.Add(new SqlParameter("@huella", SqlDbType.Char, 64) { Value = huella });
        comando.Parameters.Add(new SqlParameter("@token", SqlDbType.UniqueIdentifier) { Value = token });
        comando.Parameters.Add(new SqlParameter("@hasta", SqlDbType.DateTimeOffset) { Value = ahora.Add(TiempoMaximoEnCurso) });
        comando.Parameters.Add(new SqlParameter("@expira", SqlDbType.DateTimeOffset) { Value = ahora.Add(IClsAlmacenIdempotencia.Vigencia) });
        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            // La fila se borró entre el INSERT y el SELECT (la purga de otra réplica, con el reloj adelantado): se trata como en curso.
            return new clsFilaIdempotencia(EstadoEnCurso, huella, null, null, null, null);
        }

        return new clsFilaIdempotencia(
            lector.GetString(0),
            lector.GetString(1),
            lector.IsDBNull(2) ? null : lector.GetGuid(2),
            lector.IsDBNull(3) ? null : lector.GetString(3),
            lector.IsDBNull(4) ? null : lector.GetString(4),
            lector.IsDBNull(5) ? null : lector.GetString(5));
    }

    /// <summary>
    /// Cierra la toma: con <paramref name="resultado"/>, la clave queda <c>terminada</c>; sin él, se suelta. Si la base falla aquí, la
    /// respuesta de la operación no cambia: la toma vence sola en <see cref="TiempoMaximoEnCurso"/> y queda un warning.
    /// </summary>
    private async Task CerrarAsync(clsLlaveIdempotencia llave, Guid token, string? resultado)
    {
        try
        {
            var ahora = reloj.AhoraUtc;
            await using var conexion = await AbrirAsync(CancellationToken.None);
            await using var comando = new SqlCommand(resultado is null ? _sqlSoltar : _sqlTerminar, conexion);
            Parametros(comando, llave, ahora);
            comando.Parameters.Add(new SqlParameter("@token", SqlDbType.UniqueIdentifier) { Value = token });
            if (resultado is not null)
            {
                comando.Parameters.Add(new SqlParameter("@resultado", SqlDbType.NVarChar, -1) { Value = resultado });
                comando.Parameters.Add(new SqlParameter("@expira", SqlDbType.DateTimeOffset) { Value = ahora.Add(IClsAlmacenIdempotencia.Vigencia) });
            }

            await comando.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(
                "No se pudo {Accion} la Idempotency-Key de {Operacion} ({Tipo}): {Mensaje}. La toma vence sola en {Minutos} min.",
                resultado is null ? "soltar" : "guardar el resultado de", llave.Operacion, ex.GetType().Name, ex.Message, TiempoMaximoEnCurso.TotalMinutes);
        }
    }

    private static void Parametros(SqlCommand comando, clsLlaveIdempotencia llave, DateTimeOffset ahora)
    {
        comando.Parameters.Add(new SqlParameter("@usuarioId", SqlDbType.UniqueIdentifier) { Value = llave.UsuarioId });
        comando.Parameters.Add(new SqlParameter("@operacion", SqlDbType.NVarChar, 40) { Value = llave.Operacion });
        comando.Parameters.Add(new SqlParameter("@clave", SqlDbType.NVarChar, 64) { Value = llave.Clave });
        comando.Parameters.Add(new SqlParameter("@ahora", SqlDbType.DateTimeOffset) { Value = ahora });
    }

    private async Task<SqlConnection> AbrirAsync(CancellationToken ct)
    {
        var conexion = new SqlConnection(conexiones.Value.Cotifly);
        try
        {
            await conexion.OpenAsync(ct);
            return conexion;
        }
        catch
        {
            await conexion.DisposeAsync();
            throw;
        }
    }

    private sealed class clsSemaforoClave
    {
        public SemaphoreSlim Semaforo { get; } = new(1, 1);

        public int Usuarios { get; set; }
    }

    private sealed record clsFilaIdempotencia(string Estado, string Huella, Guid? TomadaPor, string? Resultado, string? Folio, string? SolicitudNexoId);
}

/// <summary>
/// #191: la clave de una fila de <c>CapturasIdempotentes</c>: el ámbito <c>"{operacion}:{usuarioId}"</c> de los servicios partido en sus
/// dos columnas, y la <c>Idempotency-Key</c>.
/// </summary>
public sealed record clsLlaveIdempotencia(string Operacion, Guid UsuarioId, string Clave)
{
    /// <summary>Parte <paramref name="ambito"/> (<c>"{operacion}:{usuarioId}"</c>).</summary>
    public static clsLlaveIdempotencia Desde(string ambito, string clave)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ambito);
        var separador = ambito.LastIndexOf(':');
        return separador > 0 && Guid.TryParse(ambito.AsSpan(separador + 1), out var usuarioId)
            ? new clsLlaveIdempotencia(ambito[..separador], usuarioId, clave)
            : throw new ArgumentException($"Ámbito de idempotencia sin la forma 'operacion:usuarioId': '{ambito}'.", nameof(ambito));
    }
}
