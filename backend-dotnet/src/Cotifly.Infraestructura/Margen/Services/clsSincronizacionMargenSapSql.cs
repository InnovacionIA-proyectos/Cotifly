using System.Data;
using System.Diagnostics;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Coordinación de la sincronización dentro de una réplica (HU-042.2, #243): un solo intento a la vez (el worker y la corrida forzada desde
/// la administración comparten este candado; entre réplicas lo resuelve el <c>sp_getapplock</c> del stored procedure) y el próximo chequeo
/// del worker, para estimar la próxima corrida. Singleton.
/// </summary>
public sealed class clsCoordinacionSincronizacionMargenSap : IDisposable
{
    private readonly SemaphoreSlim _candado = new(1, 1);
    private long _proximoChequeoTicks = long.MinValue;

    /// <summary>Próximo chequeo del worker de esta réplica (<c>null</c> si el worker no corre).</summary>
    public DateTimeOffset? ProximoChequeo
    {
        get
        {
            var ticks = Interlocked.Read(ref _proximoChequeoTicks);
            return ticks == long.MinValue ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        set => Interlocked.Exchange(ref _proximoChequeoTicks, value?.UtcTicks ?? long.MinValue);
    }

    /// <summary>Hay un intento en curso en esta réplica.</summary>
    public bool EnCurso => _candado.CurrentCount == 0;

    /// <summary>Toma el candado sin esperar; <c>false</c> si ya hay un intento en curso.</summary>
    public bool IntentarTomar() => _candado.Wait(0);

    /// <summary>Suelta el candado.</summary>
    public void Soltar() => _candado.Release();

    public void Dispose() => _candado.Dispose();

    /// <summary>
    /// Próxima corrida estimada: el primer chequeo del worker en o después de que la última corrida <c>ok</c> cumpla el intervalo (sin
    /// corrida <c>ok</c>, el próximo chequeo). Sin worker, cuando cumple el intervalo (o ya).
    /// </summary>
    public static DateTimeOffset ProximaCorridaEstimada(
        DateTimeOffset? ultimaOk, TimeSpan intervalo, DateTimeOffset? proximoChequeo, TimeSpan chequeo, DateTimeOffset ahora)
    {
        var vence = ultimaOk is { } ok ? ok + intervalo : ahora;
        if (proximoChequeo is not { } chequeoSiguiente)
        {
            return vence > ahora ? vence : ahora;
        }

        if (vence <= chequeoSiguiente || chequeo <= TimeSpan.Zero)
        {
            return chequeoSiguiente;
        }

        var vueltas = (long)Math.Ceiling((vence - chequeoSiguiente).Ticks / (double)chequeo.Ticks);
        return chequeoSiguiente + TimeSpan.FromTicks(chequeo.Ticks * vueltas);
    }
}

/// <summary>
/// Sincronización del margen de SAP TM hacia <c>dbo_cotifly.SapMargenCliente</c> (HU-042.2, #243, ADR-027): el port en C# del script
/// <c>db/jobs/sincronizar_margen_sap.ps1</c> de Datos, que ya no se instala. Una corrida:
/// <list type="number">
/// <item>sin forzar, revisa la última corrida <c>ok</c> de <c>SapMargenSyncLog</c> y sale si es más reciente que
/// <c>SapTm:SincronizacionMinutos</c> ("no toca": no llama a SAP);</item>
/// <item>descarga el set completo de SAP (<see cref="clsClienteRentabSap"/>) y lo valida (<see cref="clsLectorRentabClienteSap"/>);</item>
/// <item>aplica la guarda de mínimo de filas (<see cref="clsGuardaMinimoFilasSap"/>) contra las filas activas actuales;</item>
/// <item>busca el NIT por BP en el DW (<see cref="clsDirectorioNitDwOperaciones"/>); sin <c>ConnectionStrings:DwOperaciones</c> (warning una
/// vez por corrida) o sin ese BP en el DW, la fila conserva el NIT que ya tenía la copia (o queda <c>NULL</c>); configurada y caída, la
/// corrida falla;</item>
/// <item>crea <c>#SapMargenStage</c> (texto con <c>COLLATE DATABASE_DEFAULT</c>: la temporal nace con la collation del servidor y la base de
/// Cotifly tiene otra; PK <c>(salesOrgId, trqType, bpSap)</c>), la llena con <c>SqlBulkCopy</c> y llama a
/// <c>dbo_cotifly.sp_sincronizar_margen_sap</c>, todo en la MISMA conexión (la temporal es de la sesión);</item>
/// <item>lee la fila que dejó el stored procedure (<c>ok</c>, <c>omitido</c> o <c>error</c>) y la devuelve.</item>
/// </list>
/// Un fallo antes del stored procedure (SAP, validación, guarda, DW, staging) se registra aquí como <c>error</c> con un mensaje sin URL ni
/// credenciales; uno dentro del stored procedure ya lo registró él (su <c>CATCH</c>), salvo que no haya quedado su fila (sin permiso
/// <c>EXECUTE</c>, sin el objeto, un error de recompilación que su <c>TRY</c> no atrapa, tiempo agotado), que también se registra aquí.
/// Nunca lanza por esos fallos; si la base de Cotifly no responde ni para leer la última corrida ni para registrar el fallo, lanza
/// <see cref="clsSincronizacionMargenSapNoDisponibleException"/> (503 en la administración; el worker lo anota). Singleton; usa conexiones
/// propias, no el <c>DbContext</c>.
/// </summary>
public sealed class clsSincronizacionMargenSapSql(
    IOptions<clsOpcionesSapTmSincronizacion> opciones,
    IOptions<clsOpcionesConexiones> conexiones,
    clsClienteRentabSap sap,
    clsDirectorioNitDwOperaciones dw,
    clsCoordinacionSincronizacionMargenSap coordinacion,
    TimeProvider tiempo,
    ILogger<clsSincronizacionMargenSapSql> logger) : IClsSincronizacionMargenSap
{
    /// <summary>Largo de <c>SapMargenSyncLog.mensajeError</c>.</summary>
    public const int LargoMensaje = 2000;

    /// <summary>Límite de cada orden a la base de Cotifly (incluido el stored procedure), en segundos.</summary>
    public const int TiempoOrdenSegundos = 120;

    /// <summary>Errores de SQL Server que significan que el stored procedure no llegó a ejecutarse (y por tanto no registró nada).</summary>
    private static readonly HashSet<int> _spNoEjecutado = [229, 2812]; // permiso denegado; el objeto no existe

    private const string ColumnasLog = "status, iniciadoAt, finalizadoAt, filasInsertadas, filasActualizadas, filasDesactivadas, mensajeError";

    /// <summary>
    /// Límite de conexión (s) de las lecturas cortas (estado, última corrida, filas activas): con la base caída, la administración responde
    /// 503 en segundos y no en el minuto que suma el límite por defecto (15 s por intento).
    /// </summary>
    public const int LimiteConexionLecturaSegundos = 5;

    /// <summary>Límite (s) de cada orden de las lecturas cortas.</summary>
    public const int LimiteOrdenLecturaSegundos = 15;

    public bool Habilitada => opciones.Value.SincronizacionHabilitada;

    public bool Configurada => opciones.Value.Configurada;

    public string? ErrorConfiguracion => opciones.Value.ErrorConfiguracion;

    public async Task<clsEstadoSincronizacionMargenSap> EstadoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await LeerEstadoAsync(cancellationToken);
        }
        catch (Exception ex) when (EsFalloDeBase(ex, cancellationToken))
        {
            throw NoDisponible(ex);
        }
    }

    private async Task<clsEstadoSincronizacionMargenSap> LeerEstadoAsync(CancellationToken cancellationToken)
    {
        await using var conexion = await AbrirCotiflyAsync(cancellationToken, rapida: true);
        await using var comando = conexion.CreateCommand();
        comando.CommandTimeout = LimiteOrdenLecturaSegundos;
        comando.CommandText = $"""
            SELECT TOP (1) {ColumnasLog} FROM dbo_cotifly.SapMargenSyncLog ORDER BY iniciadoAt DESC;
            SELECT TOP (1) {ColumnasLog} FROM dbo_cotifly.SapMargenSyncLog WHERE status = N'ok' ORDER BY iniciadoAt DESC;
            SELECT COUNT(*) FROM dbo_cotifly.SapMargenCliente WHERE activo = 1;
            """;
        await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
        var ultima = await LeerCorridaAsync(lector, cancellationToken);
        await lector.NextResultAsync(cancellationToken);
        var ultimaOk = await LeerCorridaAsync(lector, cancellationToken);
        await lector.NextResultAsync(cancellationToken);
        await lector.ReadAsync(cancellationToken);
        var activas = lector.GetInt32(0);

        var valor = opciones.Value;
        // Deshabilitada o sin configuración válida no habrá corrida: sin estimado.
        DateTimeOffset? proxima = valor.Configurada
            ? clsCoordinacionSincronizacionMargenSap.ProximaCorridaEstimada(
                ultimaOk?.FinalizadoAt ?? ultimaOk?.IniciadoAt,
                TimeSpan.FromMinutes(valor.SincronizacionMinutos),
                coordinacion.ProximoChequeo,
                TimeSpan.FromMinutes(valor.ChequeoMinutos),
                tiempo.GetUtcNow())
            : null;
        return new clsEstadoSincronizacionMargenSap(
            ultima, ultimaOk, activas, proxima, valor.SincronizacionMinutos, valor.ChequeoMinutos, coordinacion.EnCurso, dw.Configurado,
            valor.SincronizacionHabilitada);
    }

    public async Task<clsResultadoSincronizacionMargenSap> SincronizarAsync(bool forzada, CancellationToken cancellationToken = default)
    {
        if (!Configurada)
        {
            return clsResultadoSincronizacionMargenSap.SinCorrida(clsResultadoSincronizacionMargenSap.EstadoNoConfigurada);
        }

        if (!coordinacion.IntentarTomar())
        {
            return clsResultadoSincronizacionMargenSap.SinCorrida(clsResultadoSincronizacionMargenSap.EstadoEnCurso);
        }

        try
        {
            return await SincronizarConCandadoAsync(forzada, cancellationToken);
        }
        finally
        {
            coordinacion.Soltar();
        }
    }

    private async Task<clsResultadoSincronizacionMargenSap> SincronizarConCandadoAsync(bool forzada, CancellationToken cancellationToken)
    {
        var valor = opciones.Value;
        var reloj = Stopwatch.StartNew();
        var iniciadoAt = tiempo.GetUtcNow();
        DateTimeOffset? ultimaOk = null;
        if (!forzada)
        {
            try
            {
                ultimaOk = await UltimaOkAsync(cancellationToken);
            }
            catch (Exception ex) when (EsFalloDeBase(ex, cancellationToken))
            {
                throw NoDisponible(ex);
            }
            catch (SqlException ex)
            {
                // Sin permiso (229), sin el objeto (208)...: la base responde, así que es un fallo de la corrida y queda registrado.
                return await FalloAsync(ex, iniciadoAt, 0, reloj);
            }
        }

        if (ultimaOk is not null && tiempo.GetUtcNow() - ultimaOk.Value < TimeSpan.FromMinutes(valor.SincronizacionMinutos))
        {
            logger.LogDebug("Todavía no toca sincronizar el margen de SAP TM (última corrida ok: {UltimaOk:O}).", ultimaOk);
            return clsResultadoSincronizacionMargenSap.SinCorrida(clsResultadoSincronizacionMargenSap.EstadoNoToca);
        }

        // Antes de descargar de SAP se comprueba la base de Cotifly (lectura corta): con la base caída no tiene sentido el GET a SAP, ni se
        // podría registrar el fallo.
        int activas;
        try
        {
            activas = await FilasActivasAsync(cancellationToken);
        }
        catch (Exception ex) when (EsFalloDeBase(ex, cancellationToken))
        {
            throw NoDisponible(ex);
        }
        catch (SqlException ex)
        {
            return await FalloAsync(ex, iniciadoAt, 0, reloj);
        }

        reloj.Restart();
        iniciadoAt = tiempo.GetUtcNow();
        IReadOnlyList<clsFilaMargenSap> filas = [];
        try
        {
            filas = await sap.DescargarAsync(cancellationToken);
            if (clsGuardaMinimoFilasSap.Rechazo(filas.Count, valor.MinimoFilas, valor.MinimoPorcentajeActivas, activas) is { } rechazo)
            {
                throw new clsSincronizacionMargenSapException(rechazo);
            }

            var nits = await NitsAsync(filas, cancellationToken);
            var corrida = await CargarYSincronizarAsync(filas, nits, iniciadoAt, cancellationToken);
            return Terminar(corrida, filas.Count, reloj);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is clsSincronizacionMargenSapException or SqlException or InvalidOperationException or TimeoutException)
        {
            return await FalloAsync(ex, iniciadoAt, filas.Count, reloj);
        }
    }

    /// <summary>
    /// Registra el fallo de la corrida como <c>error</c> (mensaje sin URL, credenciales ni texto de SQL Server) y lo devuelve. Si tampoco
    /// se puede registrar, la base no está disponible: <see cref="clsSincronizacionMargenSapNoDisponibleException"/>.
    /// </summary>
    private async Task<clsResultadoSincronizacionMargenSap> FalloAsync(Exception ex, DateTimeOffset iniciadoAt, int recibidas, Stopwatch reloj)
    {
        var mensaje = MensajeDe(ex);
        logger.LogDebug(ex, "Detalle del fallo de la sincronización del margen de SAP TM.");
        var corrida = await RegistrarErrorAsync(iniciadoAt, mensaje);
        if (corrida is null)
        {
            // La base de Cotifly tampoco acepta el registro: no hay corrida que informar (la administración responde 503).
            logger.LogWarning("Sincronización del margen de SAP TM: error tras {Duracion} ms sin poder registrarlo: {Mensaje}", reloj.ElapsedMilliseconds, mensaje);
            throw NoDisponible(ex);
        }

        return Terminar(corrida, recibidas, reloj);
    }

    private clsResultadoSincronizacionMargenSap Terminar(clsCorridaSincronizacionMargenSap corrida, int recibidas, Stopwatch reloj)
    {
        var duracion = (int)Math.Min(int.MaxValue, reloj.ElapsedMilliseconds);
        if (corrida.Estado == clsCorridaSincronizacionMargenSap.EstadoError)
        {
            logger.LogWarning("Sincronización del margen de SAP TM: error tras {Duracion} ms ({Recibidas} filas recibidas): {Mensaje}",
                duracion, recibidas, corrida.MensajeError);
        }
        else
        {
            logger.LogInformation(
                "Sincronización del margen de SAP TM: {Estado} en {Duracion} ms; {Recibidas} filas recibidas, {Insertadas} insertadas, {Actualizadas} actualizadas, {Desactivadas} desactivadas.",
                corrida.Estado, duracion, recibidas, corrida.FilasInsertadas, corrida.FilasActualizadas, corrida.FilasDesactivadas);
        }

        return new clsResultadoSincronizacionMargenSap(corrida.Estado, recibidas, duracion, corrida);
    }

    private async Task<IReadOnlyDictionary<string, string>?> NitsAsync(IReadOnlyList<clsFilaMargenSap> filas, CancellationToken cancellationToken)
    {
        if (!dw.Configurado)
        {
            logger.LogWarning(
                "ConnectionStrings:DwOperaciones no está configurada: cada fila del margen de SAP TM conserva el NIT que ya tenía la copia (o queda NULL).");
            return null;
        }

        try
        {
            return await dw.ObtenerNitsAsync(filas.Select(f => f.BpSap), cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            // Sin datos de conexión en el mensaje (va a SapMargenSyncLog): el detalle técnico queda en Debug.
            throw new clsSincronizacionMargenSapException(
                $"No se pudo consultar el NIT por BP en DWCubosOperacionesEBS ({ex.GetType().Name}{(ex is SqlException sql ? $" {sql.Number}" : string.Empty)}): no se tocó la tabla.",
                ex);
        }
    }

    /// <summary>
    /// <c>#SapMargenStage</c> tal como la crea el script de Datos, con <c>COLLATE DATABASE_DEFAULT</c> en el texto y la PK
    /// <c>(salesOrgId, trqType, bpSap)</c> SIN NOMBRE: en <c>tempdb</c> el nombre de una restricción es global, así que una PK con nombre
    /// hace fallar con el error 2714 la temporal de una segunda réplica que sincroniza a la vez (antes de llegar al <c>sp_getapplock</c>).
    /// </summary>
    public const string DdlStage = """
        CREATE TABLE #SapMargenStage
        (
            salesOrgId        NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
            trqType           NVARCHAR(8)   COLLATE DATABASE_DEFAULT NOT NULL,
            bpSap             NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
            nit               NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL,
            margenRecomendado DECIMAL(9, 4) NOT NULL,
            PRIMARY KEY (salesOrgId, trqType, bpSap)
        );
        """;

    /// <summary>
    /// Pasos 4 y 5 del script: <c>#SapMargenStage</c>, <c>SqlBulkCopy</c> y el stored procedure en una misma conexión. El NIT de cada fila
    /// es el del DW; si el DW no está configurado o no trae ese BP, se conserva el que ya tiene <c>SapMargenCliente</c> para la misma clave
    /// (el <c>MERGE</c> del stored procedure lo pisaría con <c>NULL</c>).
    /// </summary>
    private async Task<clsCorridaSincronizacionMargenSap> CargarYSincronizarAsync(
        IReadOnlyList<clsFilaMargenSap> filas, IReadOnlyDictionary<string, string>? nits, DateTimeOffset iniciadoAt, CancellationToken cancellationToken)
    {
        await using var conexion = await AbrirCotiflyAsync(cancellationToken);
        var existentes = await NitsExistentesAsync(conexion, cancellationToken);

        using var tabla = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        tabla.Columns.Add("salesOrgId", typeof(string));
        tabla.Columns.Add("trqType", typeof(string));
        tabla.Columns.Add("bpSap", typeof(string));
        tabla.Columns.Add("nit", typeof(string));
        tabla.Columns.Add("margenRecomendado", typeof(decimal));
        var conservados = 0;
        foreach (var fila in filas)
        {
            object nit = DBNull.Value;
            if (nits is not null && nits.TryGetValue(fila.BpSap, out var delDw))
            {
                nit = delDw;
            }
            else if (existentes.TryGetValue((fila.SalesOrgId, fila.TrqType, fila.BpSap), out var anterior))
            {
                nit = anterior;
                conservados++;
            }

            tabla.Rows.Add(fila.SalesOrgId, fila.TrqType, fila.BpSap, nit, fila.Margen);
        }

        if (conservados > 0)
        {
            logger.LogInformation("Sincronización del margen de SAP TM: {Conservados} filas conservan el NIT anterior (sin NIT en el DW).", conservados);
        }

        await using (var crear = conexion.CreateCommand())
        {
            crear.CommandTimeout = TiempoOrdenSegundos;
            crear.CommandText = DdlStage;
            await crear.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var bulk = new SqlBulkCopy(conexion) { DestinationTableName = "#SapMargenStage", BulkCopyTimeout = TiempoOrdenSegundos })
        {
            foreach (DataColumn columna in tabla.Columns)
            {
                bulk.ColumnMappings.Add(columna.ColumnName, columna.ColumnName);
            }

            await bulk.WriteToServerAsync(tabla, cancellationToken);
        }

        DateTimeOffset marca;
        await using (var ahora = conexion.CreateCommand())
        {
            ahora.CommandText = "SELECT SYSDATETIMEOFFSET();";
            marca = (DateTimeOffset)(await ahora.ExecuteScalarAsync(cancellationToken))!;
        }

        try
        {
            await using var sp = conexion.CreateCommand();
            sp.CommandText = "dbo_cotifly.sp_sincronizar_margen_sap";
            sp.CommandType = CommandType.StoredProcedure;
            sp.CommandTimeout = TiempoOrdenSegundos;
            await sp.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex) when (!ex.Errors.Cast<SqlError>().Any(e => _spNoEjecutado.Contains(e.Number)))
        {
            // Normalmente el CATCH del stored procedure ya registró el error: se devuelve esa fila, sin registrar otra. Si no la hay (un
            // error de recompilación como el 207 que su TRY no atrapa, tiempo agotado, KILL), se registra aquí (lo hace quien llama).
            logger.LogDebug(ex, "El stored procedure sp_sincronizar_margen_sap falló.");
            return await CorridaDelSpSinFallarAsync(conexion, marca)
                ?? throw new clsSincronizacionMargenSapException(
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sp_sincronizar_margen_sap falló sin registrar la corrida (SQL {ex.Number})."), ex);
        }

        return await CorridaDelSpAsync(conexion, marca, cancellationToken)
            ?? new clsCorridaSincronizacionMargenSap(clsCorridaSincronizacionMargenSap.EstadoOk, iniciadoAt, tiempo.GetUtcNow(), null, null, null, null);
    }

    /// <summary>NIT actuales de la copia por clave (sin distinguir mayúsculas, como la collation de la base).</summary>
    private static async Task<Dictionary<(string, string, string), string>> NitsExistentesAsync(SqlConnection conexion, CancellationToken cancellationToken)
    {
        var nits = new Dictionary<(string, string, string), string>(clsLectorRentabClienteSap.ComparadorClave);
        await using var comando = conexion.CreateCommand();
        comando.CommandTimeout = TiempoOrdenSegundos;
        comando.CommandText = "SELECT salesOrgId, trqType, bpSap, nit FROM dbo_cotifly.SapMargenCliente WHERE nit IS NOT NULL;";
        await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
        while (await lector.ReadAsync(cancellationToken))
        {
            nits[(lector.GetString(0), lector.GetString(1), lector.GetString(2))] = lector.GetString(3);
        }

        return nits;
    }

    /// <summary>
    /// La fila que el stored procedure acaba de escribir: la PRIMERA iniciada entre <paramref name="marca"/> (hora del servidor tomada justo
    /// antes del <c>EXEC</c>, en la misma conexión) y la hora del servidor al consultar. El stored procedure fija su <c>iniciadoAt</c> al
    /// empezar, así que su fila es la primera de ese rango; una réplica que llegó después mientras esta tenía el candado dejó su
    /// <c>omitido</c> más tarde y no se confunde (antes se tomaba la más reciente).
    /// </summary>
    private static async Task<clsCorridaSincronizacionMargenSap?> CorridaDelSpAsync(SqlConnection conexion, DateTimeOffset marca, CancellationToken cancellationToken)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandTimeout = TiempoOrdenSegundos;
        comando.CommandText = $"""
            SELECT TOP (1) {ColumnasLog} FROM dbo_cotifly.SapMargenSyncLog
            WHERE iniciadoAt >= @marca AND iniciadoAt <= SYSDATETIMEOFFSET()
            ORDER BY iniciadoAt ASC;
            """;
        comando.Parameters.Add(new SqlParameter("@marca", SqlDbType.DateTimeOffset) { Value = marca });
        await using var lector = await comando.ExecuteReaderAsync(cancellationToken);
        return await LeerCorridaAsync(lector, cancellationToken);
    }

    /// <summary><see cref="CorridaDelSpAsync"/> tras un fallo del <c>EXEC</c>: la conexión puede haber quedado inservible (<c>null</c>).</summary>
    private async Task<clsCorridaSincronizacionMargenSap?> CorridaDelSpSinFallarAsync(SqlConnection conexion, DateTimeOffset marca)
    {
        try
        {
            if (conexion.State != ConnectionState.Open)
            {
                return null;
            }

            return await CorridaDelSpAsync(conexion, marca, CancellationToken.None);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            logger.LogDebug(ex, "No se pudo leer la corrida del stored procedure tras su fallo.");
            return null;
        }
    }

    /// <summary>Paso 7 del script: deja la corrida fallida en el log. Best-effort: si tampoco esto funciona, queda en el log de la API.</summary>
    private async Task<clsCorridaSincronizacionMargenSap?> RegistrarErrorAsync(DateTimeOffset iniciadoAt, string mensaje)
    {
        try
        {
            await using var conexion = await AbrirCotiflyAsync(CancellationToken.None);
            await using var comando = conexion.CreateCommand();
            comando.CommandTimeout = TiempoOrdenSegundos;
            comando.CommandText = $"""
                INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
                OUTPUT inserted.{ColumnasLog.Replace(", ", ", inserted.", StringComparison.Ordinal)}
                VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'error', @mensaje);
                """;
            comando.Parameters.Add(new SqlParameter("@iniciadoAt", SqlDbType.DateTimeOffset) { Value = iniciadoAt });
            comando.Parameters.Add(new SqlParameter("@mensaje", SqlDbType.NVarChar, LargoMensaje) { Value = mensaje });
            await using var lector = await comando.ExecuteReaderAsync(CancellationToken.None);
            return await LeerCorridaAsync(lector, CancellationToken.None);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogError("No se pudo registrar el fallo de la sincronización del margen de SAP TM en SapMargenSyncLog ({Tipo}).", ex.GetType().Name);
            logger.LogDebug(ex, "Detalle del fallo registrando en SapMargenSyncLog.");
            return null;
        }
    }

    private async Task<DateTimeOffset?> UltimaOkAsync(CancellationToken cancellationToken)
    {
        await using var conexion = await AbrirCotiflyAsync(cancellationToken, rapida: true);
        await using var comando = conexion.CreateCommand();
        comando.CommandTimeout = LimiteOrdenLecturaSegundos;
        comando.CommandText = "SELECT TOP (1) ISNULL(finalizadoAt, iniciadoAt) FROM dbo_cotifly.SapMargenSyncLog WHERE status = N'ok' ORDER BY iniciadoAt DESC;";
        return await comando.ExecuteScalarAsync(cancellationToken) is DateTimeOffset ultima ? ultima : null;
    }

    private async Task<int> FilasActivasAsync(CancellationToken cancellationToken)
    {
        await using var conexion = await AbrirCotiflyAsync(cancellationToken, rapida: true);
        await using var comando = conexion.CreateCommand();
        comando.CommandTimeout = LimiteOrdenLecturaSegundos;
        comando.CommandText = "SELECT COUNT(*) FROM dbo_cotifly.SapMargenCliente WHERE activo = 1;";
        return (int)(await comando.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Conexión a la base de Cotifly; con <paramref name="rapida"/>, con <see cref="LimiteConexionLecturaSegundos"/> como límite.</summary>
    private async Task<SqlConnection> AbrirCotiflyAsync(CancellationToken cancellationToken, bool rapida = false)
    {
        var cadena = conexiones.Value.Cotifly;
        if (string.IsNullOrWhiteSpace(cadena))
        {
            throw new InvalidOperationException("ConnectionStrings:Cotifly no está configurada.");
        }

        if (rapida)
        {
            var builder = new SqlConnectionStringBuilder(cadena);
            builder.ConnectTimeout = Math.Min(builder.ConnectTimeout, LimiteConexionLecturaSegundos);
            cadena = builder.ConnectionString;
        }

        var conexion = new SqlConnection(cadena);
        try
        {
            await conexion.OpenAsync(cancellationToken);
            return conexion;
        }
        catch
        {
            await conexion.DisposeAsync();
            throw;
        }
    }

    /// <summary>Un fallo de la base de Cotifly (no una cancelación de quien llama).</summary>
    /// <remarks>
    /// Solo la conectividad: red, servidor o base inaccesible, login rechazado, tiempo agotado (SQL -2), un error fatal (severidad 20 o
    /// más), la cadena sin configurar. Un error de una orden con la base respondiendo (sin permiso, 229; sin el objeto, 208 o 2812) NO lo es:
    /// es un fallo de la corrida y se registra como <c>error</c>.
    /// </remarks>
    private static bool EsFalloDeBase(Exception ex, CancellationToken cancellationToken) =>
        ex switch
        {
            SqlException sql => EsConectividad(sql),
            InvalidOperationException or TimeoutException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };

    /// <summary>Errores de SQL Server de conectividad (ver <see cref="EsFalloDeBase"/>).</summary>
    private static readonly HashSet<int> _conectividad =
        [-2, -1, 2, 53, 64, 121, 233, 4060, 18452, 18456, 10053, 10054, 10060, 10061, 11001, 40613];

    private static bool EsConectividad(SqlException ex) =>
        ex.Class >= 20 || ex.Errors.Cast<SqlError>().Any(e => _conectividad.Contains(e.Number) || e.Class >= 20);

    private static clsSincronizacionMargenSapNoDisponibleException NoDisponible(Exception causa) =>
        new($"La base de Cotifly no respondió ({causa.GetType().Name}{(causa is SqlException sql ? $" {sql.Number}" : string.Empty)}).", causa);

    private static async Task<clsCorridaSincronizacionMargenSap?> LeerCorridaAsync(SqlDataReader lector, CancellationToken cancellationToken)
    {
        if (!await lector.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new clsCorridaSincronizacionMargenSap(
            lector.GetString(0),
            lector.GetDateTimeOffset(1),
            lector.IsDBNull(2) ? null : lector.GetDateTimeOffset(2),
            lector.IsDBNull(3) ? null : lector.GetInt32(3),
            lector.IsDBNull(4) ? null : lector.GetInt32(4),
            lector.IsDBNull(5) ? null : lector.GetInt32(5),
            lector.IsDBNull(6) ? null : lector.GetString(6));
    }

    /// <summary>
    /// Mensaje para <c>SapMargenSyncLog</c>: el de las fallas conocidas (ya sin datos de conexión) o, para un error de la base de Cotifly, el
    /// tipo y número de error de SQL Server (el texto de SQL Server puede nombrar el servidor). Máximo <see cref="LargoMensaje"/>.
    /// </summary>
    private static string MensajeDe(Exception ex) => Recortar(ex switch
    {
        clsSincronizacionMargenSapException => ex.Message,
        SqlException sql => $"Falló la base de Cotifly al sincronizar el margen de SAP TM (SQL {sql.Number}): no se tocó la tabla.",
        _ => $"Falló la sincronización del margen de SAP TM ({ex.GetType().Name}): no se tocó la tabla.",
    });

    private static string Recortar(string mensaje) => mensaje.Length <= LargoMensaje ? mensaje : mensaje[..LargoMensaje];
}
