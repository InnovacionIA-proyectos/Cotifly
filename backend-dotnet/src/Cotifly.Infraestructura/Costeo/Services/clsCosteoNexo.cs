using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Costeo contra Nexo en vivo (ADR-015). Rutas de <c>CONTRATO.md</c> de Nexo bajo <c>{Nexo:UrlBase}</c> (la raíz del servicio, sin
/// <c>/api</c>): <c>POST /api/captura-solicitudes</c> (HU-005.5), <c>GET /api/solicitudes/{id}</c> y
/// <c>GET /api/cotizaciones/por-solicitud/{id}</c> (HU-005.6; sin escribir en Nexo, E2E 2026-10-16) y, solo al marcar el resultado o con
/// un Nexo sin esa ruta, <c>POST /api/cotizaciones</c> ("crear o recuperar"). Igual que el resto de adaptadores de Nexo, identifica al comercial
/// con <c>X-Portal-clsUsuario-Id: {UsuariosWeb.idUsuarioExterno}</c> y nunca un token de Cotifly; se registra vía
/// <c>clsFabricaCosteoNexo</c> (sin <c>Nexo:UrlBase</c> → <see cref="clsCosteoNexoNoDisponible"/>).
/// </summary>
/// <remarks>
/// <para><b>Cliente HTTP propio (<see cref="NombreCliente"/>).</b> No usa el cliente <c>Nexo</c> de las búsquedas (8 s, 2
/// reintentos): el alta auto-costea en la misma petición (puede tardar más) y <b>un POST nunca se reintenta</b> (el alta no es
/// idempotente: un reintento automático duplicaría la solicitud en Nexo). Ver <see cref="ConfigurarResiliencia"/>.</para>
/// <para><b>Errores.</b> Nunca lanza por Nexo: tiempo agotado → <c>COSTEO_NEXO_TIEMPO_AGOTADO</c> (la solicitud pudo quedar creada);
/// 4xx → <c>COSTEO_NEXO_RECHAZADA</c> con <c>HTTP {estado} {code}: {message}</c> de Nexo en el detalle; 5xx, red, circuito abierto,
/// JSON inválido, sin configurar o comercial sin <c>idUsuarioExterno</c> → <c>COSTEO_NEXO_NO_DISPONIBLE</c>. Una línea de warning
/// por fallo, sin la traza ni el header <c>X-Portal-clsUsuario-Id</c>. La cancelación del llamador se propaga.</para>
/// </remarks>
public sealed class clsCosteoNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsCosteoNexo> logger,
    Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository? cotizaciones = null,
    Aplicacion.CatalogosFlete.Interfaces.IClsCatalogosMaestrosProvider? catalogos = null,
    Aplicacion.CatalogosFlete.Interfaces.IClsAliasNexoProvider? alias = null)
    : IClsCosteoNexo
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> del costeo (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "NexoCosteo";

    public const string MensajeNoDisponible = "El costeo de Nexo no está disponible en este momento: intenta de nuevo más tarde.";
    public const string MensajeTiempoAgotado =
        "Nexo no respondió a tiempo: la solicitud pudo quedar creada en Nexo; verifica en Nexo antes de reintentar.";
    public const string MensajeRechazada = "Nexo rechazó la solicitud.";

    /// <summary>Código de Nexo de <c>GET /api/solicitudes/{id}</c> sobre una solicitud descartada (409).</summary>
    public const string CodigoNexoDescartada = "SOLICITUD_DESCARTADA";

    /// <summary>Código de Nexo de <c>GET /api/solicitudes/{id}</c> con un id inexistente (404).</summary>
    public const string CodigoNexoNoEncontrada = "SOLICITUD_NO_ENCONTRADA";
    public const string MensajeSolicitudInvalida = "Nexo rechazó los datos de la solicitud.";

    public const string MensajeSinPermiso =
        "Tu usuario no tiene permiso en Nexo (Cabina de Fletes) para esta operación: pide al administrador de Nexo el rol Comercial o Admin.";

    /// <summary>¿Nexo respondió 403 (identidad válida sin el rol que pide la operación)?</summary>
    public static bool EsSinPermiso(clsErrorDominio error) =>
        error.Codigo == clsCodigosError.CosteoNexoNoDisponible && error.Detalle?.StartsWith("HTTP 403", StringComparison.Ordinal) == true;

    /// <summary>Límite de toda la operación y de cada intento (el alta de Nexo auto-costea en línea).</summary>
    public static readonly TimeSpan TiempoTotal = TimeSpan.FromSeconds(30);

    /// <summary>Reintentos de las consultas GET (los POST no se reintentan nunca).</summary>
    public const int ReintentosLectura = 2;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    private readonly bool _enviarGrupoRutaId = opciones.Value.EnviarGrupoRutaId;

    private readonly bool _enviarDatosCubicador = opciones.Value.EnviarDatosCubicador;

    private readonly ModoCodigosCubicador _modoCodigosCubicador = opciones.Value.ModoCodigosCubicador;

    public bool EstaConfigurado => _urlBase is not null;

    public async Task<clsResultado<clsSolicitudCreadaNexo>> EnviarSolicitudAsync(
        Guid usuarioId, clsSolicitudNexo solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        var vehiculosNexo = await VehiculosNexoAsync(usuarioId, solicitud, cancellationToken);
        var diccionario = await AliasAsync(usuarioId, solicitud, vehiculosNexo, cancellationToken);
        var cuerpo = CuerpoCaptura(solicitud, _enviarGrupoRutaId, _enviarDatosCubicador, _modoCodigosCubicador, vehiculosNexo, diccionario);
        AvisarCodigosDesconocidos(solicitud, vehiculosNexo, diccionario);
        var respuesta = await EnviarAsync(usuarioId, HttpMethod.Post, "/api/captura-solicitudes", cuerpo, cancellationToken);
        if (respuesta.EsFallo)
        {
            // Un 400 del alta es validación de los datos (Nexo no cambia de opinión con los mismos datos): error definitivo, salvo la
            // configuración de Nexo (DWCUBOS_NO_CONFIGURADO). Los códigos de captura de Nexo se traducen a los de Cotifly (G16).
            if (respuesta.Error.Codigo == clsCodigosError.CosteoNexoRechazada && respuesta.Error.Detalle?.StartsWith("HTTP 400", StringComparison.Ordinal) == true)
            {
                return TraducirRechazoCaptura(respuesta.Error.Detalle);
            }

            // Un 403 del alta: el comercial no tiene rol en Cabina de Fletes (no es una caída de Nexo; ADR-017 §4).
            return EsSinPermiso(respuesta.Error)
                ? new clsErrorDominio(clsCodigosError.NexoSinPermiso, MensajeSinPermiso, respuesta.Error.Detalle)
                : respuesta.Error;
        }

        using var documento = respuesta.Valor;
        var raiz = documento.RootElement;
        var id = Texto(raiz, "id");
        if (id is null)
        {
            return NoDisponible("respuesta sin id");
        }

        return new clsSolicitudCreadaNexo(
            id,
            Entero(raiz, "lineas") ?? solicitud.Lineas.Count,
            Entero(raiz, "fletesCalculados") ?? 0,
            Entero(raiz, "lineasPorCostear") ?? solicitud.Lineas.Count,
            raiz.TryGetProperty("costeoEnProceso", out var enProceso) && enProceso.ValueKind == JsonValueKind.True)
        {
            // Solo con "siempre" viajaron TODOS los códigos del cubicador. Con "solo_conocidos" unos viajan y otros no: se registra false
            // (el caso seguro, como con la bandera apagada), así el emparejador no compara el vehículo de los camiones del cubicador.
            CodigosCubicadorEnviados = _modoCodigosCubicador == ModoCodigosCubicador.Siempre,
            EquivalenciasVehiculo = EquivalenciasVehiculo(solicitud, _modoCodigosCubicador, vehiculosNexo, diccionario),
            ScorePrioridad = Decimal(raiz, "scorePrioridad"),
        };
    }

    public async Task<clsResultado<clsCosteoConsultadoNexo>> ConsultarCosteoAsync(
        Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudId);
        var id = solicitudId.Trim();
        var rutaSolicitud = $"/api/solicitudes/{Uri.EscapeDataString(id)}";
        var solicitud = await EnviarAsync(usuarioId, HttpMethod.Get, rutaSolicitud, null, cancellationToken);
        if (solicitud.EsFallo && EsConflictoPasajero(solicitud.Error))
        {
            // E2E 2026-10-16 (observación 2): "abrir" la solicitud en Nexo cambia su estado, y dos lecturas a la vez (el polling y la Mesa
            // o el comercial) chocan con un 409 de concurrencia que al reintentar da 200. Se reintenta UNA vez, tras una espera corta.
            logger.LogInformation("Nexo respondió un 409 pasajero a GET {Ruta}; se reintenta una vez.", rutaSolicitud);
            await Task.Delay(EsperaConflictoPasajero, cancellationToken);
            solicitud = await EnviarAsync(usuarioId, HttpMethod.Get, rutaSolicitud, null, cancellationToken);
        }

        if (solicitud.EsFallo)
        {
            // ObtenerCosteoQuery de Nexo responde 409 SOLICITUD_DESCARTADA (nunca 200 con "Descartada") y 404
            // SOLICITUD_NO_ENCONTRADA si el id ya no existe: ambos terminan el polling.
            var detalle = solicitud.Error.Detalle ?? string.Empty;
            if (detalle.StartsWith("HTTP 409 " + CodigoNexoDescartada, StringComparison.Ordinal))
            {
                return new clsCosteoConsultadoNexo(clsCosteoConsultadoNexo.SolicitudDescartada, [], null);
            }

            if (detalle.StartsWith("HTTP 404 " + CodigoNexoNoEncontrada, StringComparison.Ordinal))
            {
                return new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudNoEncontrada,
                    $"Nexo ya no reconoce la solicitud {id}.", detalle);
            }

            return solicitud.Error;
        }

        string estado;
        List<string> lineas;
        List<clsLineaSolicitudNexo> contenidoLineas;
        using (var documento = solicitud.Valor)
        {
            // lineas[] de primer nivel (LineaCosteoDto): el contenido de cada línea para asociarla por contenido (ADR-017 §3, #134).
            contenidoLineas = documento.RootElement.ValueKind == JsonValueKind.Object && documento.RootElement.TryGetProperty("lineas", out var d)
                      && d.ValueKind == JsonValueKind.Array
                ? [.. d.EnumerateArray()
                    .Where(x => Texto(x, "lineaId") is not null)
                    .Select(LeerLineaSolicitud)]
                : [];
            var cabecera = documento.RootElement.ValueKind == JsonValueKind.Object && documento.RootElement.TryGetProperty("solicitud", out var s)
                ? s
                : default;
            estado = Texto(cabecera, "estado") ?? string.Empty;
            lineas = cabecera.ValueKind == JsonValueKind.Object && cabecera.TryGetProperty("lineas", out var l) && l.ValueKind == JsonValueKind.Array
                ? [.. l.EnumerateArray().Select(x => Texto(x, "id")).OfType<string>()]
                : [];
        }

        if (estado.Length == 0)
        {
            return NoDisponible("respuesta de la solicitud sin estado");
        }

        if (string.Equals(estado, clsCosteoConsultadoNexo.SolicitudDescartada, StringComparison.OrdinalIgnoreCase))
        {
            return new clsCosteoConsultadoNexo(estado, lineas, null) { Detalle = contenidoLineas, CodigosCubicadorEnviados = await CodigosEnviadosAsync(id, cancellationToken) };
        }

        var (cotizacion, error) = await CotizacionDeSolicitudAsync(usuarioId, id, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        return new clsCosteoConsultadoNexo(estado, lineas, cotizacion)
        {
            Detalle = contenidoLineas,
            CodigosCubicadorEnviados = await CodigosEnviadosAsync(id, cancellationToken),
            EquivalenciasVehiculo = await EquivalenciasEnviadasAsync(id, cancellationToken),
        };
    }

    /// <summary>
    /// HU-031.1 (#194): los códigos de vehículo que viajaron traducidos por el alias en el alta de <paramref name="solicitudId"/> (lo que
    /// registró el envío). Vacío sin repositorio, sin registro o si la lectura falla: entonces se compara como antes (sin traducción).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> EquivalenciasEnviadasAsync(string solicitudId, CancellationToken cancellationToken)
    {
        if (cotizaciones is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return await cotizaciones.ObtenerEquivalenciasVehiculoAsync(solicitudId, cancellationToken) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("No se pudieron leer los vehículos traducidos por alias de la solicitud {Solicitud} ({Tipo}): se compara sin ellos.",
                solicitudId, ex.GetType().Name);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>El <c>cotizacionNexoId</c> guardado para la solicitud, o <c>null</c> (sin repositorio o si la lectura falla).</summary>
    private async Task<string?> CotizacionGuardadaAsync(string solicitudId, CancellationToken cancellationToken)
    {
        if (cotizaciones is null)
        {
            return null;
        }

        try
        {
            return await cotizaciones.ObtenerCotizacionNexoIdAsync(solicitudId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("No se pudo leer el id de la cotización de Nexo de la solicitud {Solicitud} ({Tipo}): se busca por la solicitud.",
                solicitudId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Espera antes de repetir un <c>GET /api/solicitudes/{id}</c> que chocó con otra lectura (409 pasajero).</summary>
    public static readonly TimeSpan EsperaConflictoPasajero = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// ¿Un 409 de <c>GET /api/solicitudes/{id}</c> que no es <c>SOLICITUD_DESCARTADA</c>? Es el choque de dos lecturas (el GET de Nexo
    /// "abre" la solicitud y cambia su estado): se reintenta una vez.
    /// </summary>
    private static bool EsConflictoPasajero(clsErrorDominio error) =>
        error.Codigo == clsCodigosError.CosteoNexoRechazada
        && error.Detalle?.StartsWith("HTTP 409", StringComparison.Ordinal) == true
        && !error.Detalle.StartsWith("HTTP 409 " + CodigoNexoDescartada, StringComparison.Ordinal);

    /// <summary>Ruta de Nexo que devuelve la cotización de una solicitud SIN crearla (S5-HU-20.1, #286): 200 con ella o 204 si no hay.</summary>
    public const string RutaCotizacionPorSolicitud = "/api/cotizaciones/por-solicitud/";

    /// <summary>
    /// La cotización de Nexo de la solicitud SIN escribir en Nexo (E2E 2026-10-16: el polling hacía un <c>POST /api/cotizaciones</c>,
    /// "crear o recuperar", en cada ciclo: 69 escrituras en 20 minutos para 5 solicitudes).
    /// <list type="number">
    /// <item>Con el id ya guardado (<c>cotizacionNexoId</c>, lo guarda el polling la primera vez que la ve): <c>GET /api/cotizaciones/{id}</c>.
    /// Si esa lectura no la trae (Nexo ya no la reconoce o falló), se busca por la solicitud.</item>
    /// <item><c>GET /api/cotizaciones/por-solicitud/{id}</c>: 200 → la cotización (con sus fletes y conexos); 204 → <c>null</c> (Nexo
    /// todavía no la crea: no hay fletes; la crean el autocosteo o la Mesa al costear).</item>
    /// <item>Solo si esa ruta no existe (404/405 sin código de Nexo: un Nexo anterior a #286) se usa el "crear o recuperar" de siempre, que
    /// con el id guardado ocurre una sola vez por solicitud.</item>
    /// </list>
    /// </summary>
    private async Task<(clsCotizacionNexo? Cotizacion, clsErrorDominio? Error)> CotizacionDeSolicitudAsync(
        Guid usuarioId, string solicitudId, CancellationToken cancellationToken)
    {
        if (await CotizacionGuardadaAsync(solicitudId, cancellationToken) is { Length: > 0 } cotizacionNexoId)
        {
            var porId = await EnviarAsync(usuarioId, HttpMethod.Get, $"/api/cotizaciones/{Uri.EscapeDataString(cotizacionNexoId)}", null, cancellationToken);
            if (porId.EsExitoso)
            {
                using var documento = porId.Valor;
                if (LeerCotizacion(documento.RootElement) is { } guardada)
                {
                    return (guardada, null);
                }
            }

            // Si Nexo no la devuelve (ya no la reconoce, o falló esa lectura), se busca por la solicitud: otra lectura, nunca una escritura.
            logger.LogWarning("Nexo no devolvió la cotización {Cotizacion} guardada para la solicitud {Solicitud} ({Codigo}): se busca por la solicitud.",
                cotizacionNexoId, solicitudId, porId.EsFallo ? porId.Error.Codigo : "sin id");
        }

        var ruta = RutaCotizacionPorSolicitud + Uri.EscapeDataString(solicitudId);
        var leida = await EnviarAsync(usuarioId, HttpMethod.Get, ruta, null, cancellationToken, vacioEsNulo: true);
        if (leida.EsFallo)
        {
            var detalle = leida.Error.Detalle ?? string.Empty;
            if (leida.Error.Codigo == clsCodigosError.CosteoNexoRechazada
                && (detalle.StartsWith("HTTP 404", StringComparison.Ordinal) || detalle.StartsWith("HTTP 405", StringComparison.Ordinal))
                && CodigoNexo(detalle) is null or "RUTA_NO_ENCONTRADA")
            {
                logger.LogWarning("Nexo no publica GET {Ruta} ({Detalle}): se usa POST /api/cotizaciones (crear o recuperar).", RutaCotizacionPorSolicitud, detalle);
                var creada = await ObtenerCotizacionAsync(usuarioId, solicitudId, cancellationToken);
                return creada.EsFallo ? (null, creada.Error) : (creada.Valor, null);
            }

            return (null, leida.Error);
        }

        using var cuerpo = leida.Valor;
        if (cuerpo.RootElement.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        var cotizacion = LeerCotizacion(cuerpo.RootElement);
        return cotizacion is null ? (null, NoDisponible("respuesta de la cotización sin id")) : (cotizacion, null);
    }

    /// <summary>
    /// ¿Viajaron los códigos del cubicador en el alta de <paramref name="solicitudId"/>? Lo que registró el envío
    /// (<see cref="clsEnvioNexoRegistrado"/>), NUNCA la bandera actual (la validación de #159 midió 110 a 140 cruces de costo al leer con la
    /// bandera cambiada). Sin registro, <c>false</c>: no se compara el vehículo de los camiones del cubicador (el caso seguro).
    /// </summary>
    private async Task<bool> CodigosEnviadosAsync(string solicitudId, CancellationToken cancellationToken)
    {
        if (cotizaciones is null)
        {
            return false;
        }

        try
        {
            return await cotizaciones.ObtenerCodigosCubicadorEnviadosAsync(solicitudId, cancellationToken) ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("No se pudo leer si viajaron los códigos del cubicador de la solicitud {Solicitud} ({Tipo}): se asume que no.",
                solicitudId, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// "Crear o recuperar": devuelve la entregada, el borrador existente o uno vacío nuevo (lo mismo que hace la Mesa de Nexo al abrir la
    /// solicitud). ES UNA ESCRITURA en Nexo: el polling ya no la usa (lee con <see cref="RutaCotizacionPorSolicitud"/>); queda para marcar el
    /// resultado cuando Cotifly todavía no conoce el id de la cotización y para un Nexo sin la ruta de lectura.
    /// </summary>
    public async Task<clsResultado<clsCotizacionNexo>> ObtenerCotizacionAsync(Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudId);
        var cotizacion = await EnviarAsync(usuarioId, HttpMethod.Post, "/api/cotizaciones", new { solicitudId = solicitudId.Trim() }, cancellationToken);
        if (cotizacion.EsFallo)
        {
            return cotizacion.Error;
        }

        using var cuerpo = cotizacion.Valor;
        var leida = LeerCotizacion(cuerpo.RootElement);
        return leida is null ? NoDisponible("respuesta de la cotización sin id") : leida;
    }

    public async Task<clsResultado<clsCotizacionNexo>> MarcarResultadoAsync(
        Guid usuarioId, string cotizacionNexoId, clsResultadoNexo resultado, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cotizacionNexoId);
        ArgumentNullException.ThrowIfNull(resultado);
        var cuerpo = new clsResultadoWire(resultado.Ganada ? "ganada" : "perdida", resultado.Motivo, resultado.Comentario);
        var respuesta = await EnviarAsync(
            usuarioId, HttpMethod.Post, $"/api/cotizaciones/{Uri.EscapeDataString(cotizacionNexoId.Trim())}/resultado", cuerpo, cancellationToken);
        if (respuesta.EsFallo)
        {
            return respuesta.Error;
        }

        using var documento = respuesta.Valor;
        var leida = LeerCotizacion(documento.RootElement);
        return leida is null ? NoDisponible("respuesta del resultado sin id") : leida;
    }

    /// <summary>Código de Nexo de <c>POST /api/solicitudes/{id}/rechazar</c> sobre una solicitud ya descartada (409).</summary>
    public const string CodigoNexoYaDescartada = "SOLICITUD_YA_DESCARTADA";

    /// <summary>Largo máximo del <c>motivo</c> del rechazo en Nexo (<c>clsRechazarSolicitudRequest</c>).</summary>
    public const int LargoMotivoRechazo = 100;

    /// <summary>Largo máximo del <c>comentario</c> del rechazo en Nexo.</summary>
    public const int LargoComentarioRechazo = 500;

    public async Task<clsResultado<ResultadoRechazoNexo>> RechazarSolicitudAsync(
        Guid usuarioId, string solicitudId, string motivo, string? comentario, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudId);
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        var cuerpo = new clsRechazoWire(Recortar(motivo, LargoMotivoRechazo)!, Recortar(comentario, LargoComentarioRechazo));
        var respuesta = await EnviarAsync(
            usuarioId, HttpMethod.Post, $"/api/solicitudes/{Uri.EscapeDataString(solicitudId.Trim())}/rechazar", cuerpo, cancellationToken, vacioEsNulo: true);
        if (respuesta.EsExitoso)
        {
            respuesta.Valor.Dispose();
            return ResultadoRechazoNexo.Descartada;
        }

        var error = respuesta.Error;
        var codigo = error.Detalle is { } detalle ? CodigoNexo(detalle) : null;
        if (error.Codigo == clsCodigosError.CosteoNexoRechazada && error.Detalle?.StartsWith("HTTP 409", StringComparison.Ordinal) == true
            && codigo == CodigoNexoYaDescartada)
        {
            return ResultadoRechazoNexo.YaDescartada;
        }

        if (error.Codigo == clsCodigosError.CosteoNexoRechazada && error.Detalle?.StartsWith("HTTP 404", StringComparison.Ordinal) == true
            && codigo == CodigoNexoNoEncontrada)
        {
            return ResultadoRechazoNexo.NoEncontrada;
        }

        return EsSinPermiso(error) ? new clsErrorDominio(clsCodigosError.NexoSinPermiso, MensajeSinPermiso, error.Detalle) : error;
    }

    /// <summary>El texto sin espacios al borde y recortado a <paramref name="largo"/>; <c>null</c> si queda vacío.</summary>
    private static string? Recortar(string? texto, int largo)
    {
        var limpio = texto?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio.Length <= largo ? limpio : limpio[..largo];
    }

    /// <summary>Códigos 400 de <c>POST /api/captura-solicitudes</c> de catálogo, línea o peso (G16): los datos de la solicitud.</summary>
    public static readonly IReadOnlySet<string> CodigosCapturaDatos =
        new HashSet<string>(StringComparer.Ordinal) { "ENUM_INVALIDO", "LINEA_INCOMPLETA", "PESO_REQUERIDO", "PESO_INCONSISTENTE" };

    /// <summary>
    /// Códigos 400 de la captura sobre el cliente (G16): BP o id que Nexo no reconoce. Reintentables: el reintento vuelve a resolver el
    /// cliente en el catálogo de Nexo, así que una corrección en SAP o Nexo lo destraba.
    /// </summary>
    public static readonly IReadOnlySet<string> CodigosCapturaCliente =
        new HashSet<string>(StringComparer.Ordinal) { "CLIENTE_REQUERIDO", "CLIENTE_INVALIDO", "CLIENTE_BP_INVALIDO" };

    /// <summary>Código 400 de la captura cuando Nexo no tiene configurado DWCubos: es configuración de Nexo, reintentable.</summary>
    public const string CodigoDwCubosNoConfigurado = "DWCUBOS_NO_CONFIGURADO";

    public const string MensajeCapturaDatos =
        "Nexo rechazó los datos de la solicitud (catálogo, línea o peso): corrige el formulario y crea la cotización de nuevo.";

    public const string MensajeCapturaCliente =
        "Nexo no reconoce el cliente de la solicitud: revisa el BP del cliente en SAP/Nexo y reintenta el envío.";

    public const string MensajeDwCubos =
        "Nexo no tiene configurado el cálculo de cubicaje (DWCubos): intenta de nuevo más tarde o avisa al equipo de Nexo.";

    /// <summary>
    /// Traduce un 400 del alta (G16, <c>docs/conexion_nexo/HU-013-preguntas-nexo.md</c> §3): <c>ENUM_INVALIDO</c>, <c>LINEA_INCOMPLETA</c>,
    /// <c>PESO_REQUERIDO</c> y <c>PESO_INCONSISTENTE</c> → <c>SOLICITUD_COSTEO_INVALIDA</c> (definitivo); <c>CLIENTE_REQUERIDO</c>,
    /// <c>CLIENTE_INVALIDO</c> y <c>CLIENTE_BP_INVALIDO</c> → <c>COSTEO_NEXO_RECHAZADA</c> con un motivo de cliente (reintentable);
    /// <c>DWCUBOS_NO_CONFIGURADO</c> → <c>COSTEO_NEXO_NO_DISPONIBLE</c> (reintentable); cualquier otro (<c>VALIDATION_ERROR</c>...) →
    /// <c>COSTEO_NEXO_SOLICITUD_INVALIDA</c>. El detalle conserva <c>HTTP 400 {code}: {message}</c> de Nexo.
    /// </summary>
    public static clsErrorDominio TraducirRechazoCaptura(string detalle)
    {
        ArgumentNullException.ThrowIfNull(detalle);
        var codigo = CodigoNexo(detalle);
        return codigo switch
        {
            not null when CodigosCapturaDatos.Contains(codigo) => new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, MensajeCapturaDatos, detalle),
            not null when CodigosCapturaCliente.Contains(codigo) => new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, MensajeCapturaCliente, detalle),
            CodigoDwCubosNoConfigurado => new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, MensajeDwCubos, detalle),
            _ => new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudInvalida, MensajeSolicitudInvalida, detalle),
        };
    }

    /// <summary>El <c>code</c> de Nexo de un detalle <c>HTTP {estado} {code}: {message}</c>, o <c>null</c> si no trae.</summary>
    internal static string? CodigoNexo(string detalle)
    {
        var partes = detalle.Split(' ', 3);
        if (partes.Length < 3 || partes[1].EndsWith(':'))
        {
            return null;
        }

        var codigo = partes[2].Split(':', 2)[0].Trim();
        return codigo.Length == 0 || codigo.Contains(' ', StringComparison.Ordinal) ? null : codigo;
    }

    /// <summary><c>CotizacionDto</c> de Nexo, o <c>null</c> si no trae <c>id</c>. Líneas sin <c>valorFlete</c> se descartan.</summary>
    internal static clsCotizacionNexo? LeerCotizacion(JsonElement raiz)
    {
        if (Texto(raiz, "id") is not { } id)
        {
            return null;
        }

        var lineas = new List<clsLineaCosteoNexo>();
        if (raiz.TryGetProperty("lineas", out var l) && l.ValueKind == JsonValueKind.Array)
        {
            foreach (var linea in l.EnumerateArray())
            {
                if (Decimal(linea, "valorFlete") is { } flete)
                {
                    // id = la línea de la COTIZACIÓN de Nexo (clsCotizacionLineaDto.id): el lineaId de los conexos (HU-013.3).
                    lineas.Add(new clsLineaCosteoNexo(
                        Texto(linea, "lineaSolicitudId"), flete, Decimal(linea, "valorAprovisionamiento"), Decimal(linea, "valorRetornoContenedor"), Texto(linea, "id")));
                }
            }
        }

        var conexos = new List<clsConexoNexo>();
        if (raiz.TryGetProperty("conexos", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var conexo in c.EnumerateArray())
            {
                if (Texto(conexo, "id") is { } conexoId && Decimal(conexo, "valorAplicado") is { } valor)
                {
                    conexos.Add(new clsConexoNexo(conexoId, Texto(conexo, "nombre"), Texto(conexo, "categoria") ?? "logistico", Decimal(conexo, "cantidad") ?? 1m, valor)
                    {
                        CotizacionLineaId = Texto(conexo, "cotizacionLineaId"),
                        ServicioConexoId = Texto(conexo, "servicioConexoId"),
                        CategoriaInformada = Texto(conexo, "categoria"),
                    });
                }
            }
        }

        return new clsCotizacionNexo(id, Texto(raiz, "estado") ?? clsCotizacionNexo.EstadoBorrador, Texto(raiz, "resultado") ?? "SinDefinir", lineas, conexos);
    }

    /// <summary>
    /// Una línea de <c>lineas[]</c> de <c>SolicitudCosteoDto</c> (<c>LineaCosteoDto</c>). La ruta <c>"Origen → Destino"</c> viaja en
    /// <c>ruta</c>; el swagger de Nexo QA (2026-10-08) la publica como <c>clsRuta</c> (renombre de su DTO), así que se aceptan los dos
    /// nombres. <c>grupoRutaId</c> es el id de la ruta de Cotifly e <c>idRecomendacionVehiculo</c> el camión del cubicador que Nexo guardó
    /// en la línea (HU-023.4, #159; <c>null</c> si la línea viajó sin ellos). <c>grupoRutaId</c> no es su <c>rutaId</c>, que es de su
    /// catálogo de rutas. #181 (Nexo #309): <c>estadoLinea</c> y <c>motivoPendiente</c> van al final de la línea y son opcionales; si Nexo no
    /// los manda (versión anterior), o no son texto, quedan en <c>null</c> sin error. Solo se muestran: el emparejamiento no los usa.
    /// </summary>
    internal static clsLineaSolicitudNexo LeerLineaSolicitud(JsonElement linea) =>
        new(Texto(linea, "lineaId") ?? string.Empty, Texto(linea, "ruta") ?? Texto(linea, "clsRuta"), Texto(linea, "tipologia"), Decimal(linea, "peso"),
            Texto(linea, "tipoVehiculo"), Texto(linea, "carroceria"))
        {
            GrupoRutaId = Texto(linea, "grupoRutaId"),
            IdRecomendacionVehiculo = Texto(linea, "idRecomendacionVehiculo"),
            NumeroLinea = Entero(linea, "numeroLinea") ?? Entero(linea, "orden"),
            EstadoLinea = Cadena(linea, "estadoLinea"),
            MotivoPendiente = Cadena(linea, "motivoPendiente"),
        };

    /// <summary>La propiedad si es un texto; <c>null</c> si falta, es <c>null</c> o es de otro tipo (#181: campos opcionales de Nexo).</summary>
    internal static string? Cadena(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;

    /// <summary><c>{urlBase}{ruta}</c>, con <paramref name="ruta"/> empezando por <c>/api/</c> (la base no lleva <c>/api</c>).</summary>
    public static Uri ConstruirUrl(string urlBase, string ruta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        return new Uri($"{urlBase.TrimEnd('/')}{ruta}", UriKind.Absolute);
    }

    /// <summary>
    /// Tubería del cliente <see cref="NombreCliente"/>: 30 s en total y por intento, reintentos solo para métodos seguros (GET) y el
    /// circuito con una ventana de muestreo de al menos el doble del intento (lo exige la tubería estándar).
    /// </summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.TotalRequestTimeout.Timeout = TiempoTotal;
        resiliencia.AttemptTimeout.Timeout = TiempoTotal;
        resiliencia.Retry.MaxRetryAttempts = ReintentosLectura;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(300);
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        // HU-023.4 (#159): un 429 no se reintenta (respeta el Retry-After vía clsCupoNexoHandler). Antes de DisableForUnsafeHttpMethods,
        // que compone sobre el ShouldHandle vigente.
        Comun.clsCupoNexoHandler.NoReintentar429(resiliencia);
        resiliencia.Retry.DisableForUnsafeHttpMethods();
        resiliencia.CircuitBreaker.SamplingDuration = TiempoTotal * 2;
    }

    /// <summary>
    /// <c>CapturaSolicitudRequest</c> de Nexo (camelCase) con sus líneas en orden (una por camión, ruta por ruta; HU-021, ADR-019). Sin
    /// <c>valorEstimado</c> ni <c>referencia</c> (decisión 1 del 2026-10-08: se eliminan y dejan de enviarse a Nexo):
    /// <c>tipo</c> en minúscula (<c>spot</c> | <c>licitacion</c>, como lo valida Nexo), el cliente por BP o por id (G14), la línea de
    /// negocio de Nexo de la ruta de cada línea (#134; si no la trae, la de la solicitud) y los catálogos con el código de Nexo tal cual. HU-013.10: <c>notas</c> en el encabezado (G7),
    /// <c>origenMunicipioId</c>/<c>destinoMunicipioId</c> por línea (G6) y en cada línea las horas de cargue y descargue de la línea
    /// (HU-013.11, filas de una licitación) o, si no las trae, las del encabezado (G5: sin ellas Nexo aplica a la línea su valor por
    /// defecto, 4 h + 4 h). <c>deadline</c> viaja en ISO 8601 con su
    /// desplazamiento (G4). Los nulos no viajan. HU-023.4 (#159): con <paramref name="enviarGrupoRutaId"/> (<c>Nexo:EnviarGrupoRutaId</c>)
    /// cada línea lleva <c>grupoRutaId</c> = el id de su ruta de Cotifly; con <paramref name="enviarDatosCubicador"/>
    /// (<c>Nexo:EnviarDatosCubicador</c>) cada línea lleva <c>idRecomendacionVehiculo</c> (el camión del cubicador). Las dos encendidas por
    /// defecto desde #176 (Nexo ya las desplegó); quedan como interruptor de emergencia. Contrato con Nexo #297
    /// (S5-HU-20.5): el código de mercancía NO viaja (ni <c>codigoTipoMercancia</c>); no hay campo de orden de entrada: Nexo numera las
    /// líneas por el orden del arreglo (<c>numeroLinea</c> en el GET), que aquí es ruta por ruta y, dentro de cada una, el orden de los
    /// camiones. El vehículo y la carrocería de cada línea los decide <see cref="VehiculoLinea"/> según <paramref name="modoCodigosCubicador"/>
    /// (<c>Nexo:EnviarCodigosCubicador</c>, <c>siempre</c> por defecto desde HU-031.1 #194), <paramref name="vehiculosNexo"/> (los
    /// códigos de <c>GET /api/catalogos/tipos-vehiculo</c>; <c>null</c> si no están disponibles) y <paramref name="alias"/> (el diccionario de alias de Nexo, #194; <c>null</c> si no hace falta o no está disponible). Los valores por defecto de los parámetros
    /// de este método son para las pruebas; el adaptador pasa siempre los de <see cref="clsOpcionesNexo"/> y el catálogo.
    /// </summary>
    internal static object CuerpoCaptura(
        clsSolicitudNexo solicitud, bool enviarGrupoRutaId = true, bool enviarDatosCubicador = true,
        ModoCodigosCubicador modoCodigosCubicador = ModoCodigosCubicador.Siempre, IReadOnlyCollection<string>? vehiculosNexo = null,
        Aplicacion.CatalogosFlete.DTOs.clsDiccionarioAliasNexo? alias = null)
    {
        var e = solicitud.Encabezado;
        // HU-023 (#176, contrato con Nexo §3.3): una ruta con un solo camión es una "línea suelta" y viaja con grupoRutaId = null.
        var camionesPorRuta = solicitud.Lineas.Where(l => l.RutaId is not null).GroupBy(l => l.RutaId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return new clsCapturaSolicitudWire(
            solicitud.Cliente.Bp,
            solicitud.Cliente.Id,
            clsCodigoEnum.ACodigo(e.Tipo),
            e.Deadline,
            e.HorasCargue,
            e.HorasDescargue,
            string.IsNullOrWhiteSpace(e.Notas) ? null : e.Notas.Trim(),
            [.. solicitud.Lineas.Select(s =>
            {
                var (tipoVehiculo, carroceria) = VehiculoLinea(s, modoCodigosCubicador, vehiculosNexo, alias);
                return new clsCapturaLineaWire(
                s.Origen, s.Destino, s.OrigenMunicipioId, s.DestinoMunicipioId,
                tipoVehiculo,
                carroceria,
                s.Tipologia,
                s.LineaNegocioNexo ?? solicitud.LineaNegocioNexo, s.ClaseViaje, s.ClaseContenedor,
                s.Exclusivo, s.Consolidado, s.RetornaContenedor, s.CiudadDevolucionContenedor, s.LevanteContenedorDiferente,
                s.CiudadLevanteContenedor, s.ValorAprovisionamiento, s.HorasCargue ?? e.HorasCargue, s.HorasDescargue ?? e.HorasDescargue, s.PesoKg, s.ValorMercancia,
                s.Posiciones.Count == 0
                    ? null
                    : [.. s.Posiciones.Select(p => new clsCapturaPosicionWire(p.Descripcion, p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg))],
                GrupoRutaId(s, enviarGrupoRutaId, camionesPorRuta),
                enviarDatosCubicador ? s.IdRecomendacionVehiculo?.ToString("D") : null);
            })]);
    }

    /// <summary>
    /// <c>grupoRutaId</c> de la línea (HU-023, #176): el id de su ruta de Cotifly (un GUID por recomendación, igual en todas sus líneas,
    /// que van contiguas porque las líneas se arman ruta por ruta) solo si la ruta tiene más de un camión; <c>null</c> para una línea suelta
    /// o con <c>Nexo:EnviarGrupoRutaId</c> apagada.
    /// </summary>
    internal static string? GrupoRutaId(clsLineaSolicitudCosteo linea, bool enviarGrupoRutaId, IReadOnlyDictionary<Guid, int> camionesPorRuta) =>
        enviarGrupoRutaId && linea.RutaId is { } rutaId && camionesPorRuta.TryGetValue(rutaId, out var camiones) && camiones > 1
            ? rutaId.ToString("D")
            : null;

    /// <summary>
    /// El <c>tipoVehiculo</c> y la <c>carroceria</c> que viajan en la línea (HU-014 #188; HU-031.1 #194, decisión del usuario del
    /// 2026-10-20). El vehículo se resuelve en este orden: (1) el catálogo oficial de Nexo (<c>GET /api/catalogos/tipos-vehiculo</c>, con la
    /// equivalencia de <see cref="clsCodigosConocidosNexo"/>: exacto o, sin ambigüedad, sin tildes, mayúsculas ni <c>_</c>/<c>-</c>); (2) el
    /// diccionario de alias de Nexo (NEXO #321: <c>C2</c> → <c>2</c>, <c>TB</c> → <c>2L2</c>). Lo que se resuelve viaja ESCRITO COMO EN
    /// NEXO (así Nexo lo reconoce sin depender de su normalización, no nace en cuarentena y la línea se autocostea). Cotifly conserva su
    /// código (el del cubicador): el emparejamiento y la regla "la Mesa manda" (D2) comparan contra el código que viajó
    /// (<see cref="clsSolicitudCreadaNexo.EquivalenciasVehiculo"/>, <see cref="EquivalenciasVehiculo"/>).
    /// <list type="bullet">
    /// <item><b>Camión del cubicador</b> (con <c>idRecomendacionVehiculo</c>): <c>nunca</c> → sin vehículo ni carrocería (interruptor de
    /// emergencia: ni siquiera se consulta el alias); <c>siempre</c> (por defecto desde #194) → el código resuelto o, si ni el catálogo ni
    /// el alias lo conocen, tal cual (Nexo lo pone en cuarentena y lo normaliza en su bandeja, NEXO #322); <c>solo_conocidos</c> → el
    /// vehículo solo si se resolvió y, solo entonces, la carrocería si está en la lista de carrocerías de Nexo
    /// (<see cref="clsCatalogosCaptura.Carrocerias"/>: Nexo no publica un catálogo de carrocerías y el diccionario no las cubre); si no,
    /// la línea va sin vehículo ni carrocería (la resuelve la Mesa).</item>
    /// <item><b>Camión sin recomendación</b> (el vehículo digitado del respaldo, <c>Captura:AceptarVehiculoCliente</c>): la captura ya
    /// exigió que sea un código de Nexo o un alias que resuelve a uno (<c>clsValidadorCatalogosCaptura</c>), así que viaja siempre,
    /// resuelto si se puede y si no tal cual. No se quita: el emparejamiento compara su vehículo con el de la línea de Nexo.</item>
    /// </list>
    /// La carrocería, en la forma de Nexo si está en su lista y si no tal cual (con <c>siempre</c> o en el respaldo).
    /// </summary>
    internal static (string? TipoVehiculo, string? Carroceria) VehiculoLinea(
        clsLineaSolicitudCosteo linea, ModoCodigosCubicador modo, IReadOnlyCollection<string>? vehiculosNexo,
        Aplicacion.CatalogosFlete.DTOs.clsDiccionarioAliasNexo? alias = null)
    {
        var tipo = string.IsNullOrWhiteSpace(linea.TipoVehiculo) ? null : linea.TipoVehiculo;
        var carroceria = string.IsNullOrWhiteSpace(linea.Carroceria) ? null : linea.Carroceria;
        if (linea.IdRecomendacionVehiculo is not null && modo == ModoCodigosCubicador.Nunca)
        {
            return (null, null);
        }

        var tipoNexo = Aplicacion.CatalogosFlete.Services.clsResolucionCodigosNexo.Vehiculo(vehiculosNexo, alias, tipo)?.Codigo;
        var carroceriaNexo = clsCodigosConocidosNexo.Carroceria(carroceria);
        if (linea.IdRecomendacionVehiculo is null || modo == ModoCodigosCubicador.Siempre)
        {
            return (tipoNexo ?? tipo, carroceriaNexo ?? carroceria);
        }

        return tipoNexo is not null ? (tipoNexo, carroceriaNexo) : (null, null);
    }

    /// <summary>
    /// HU-031.1 (#194): los códigos de vehículo de Cotifly que viajaron con OTRO código porque el alias los tradujo (<c>C2</c> → <c>2</c>).
    /// No incluye la misma sigla escrita de otra forma (<c>2s3</c> → <c>2S3</c>): esa ya la reconoce
    /// <see cref="clsVehiculoLineaSolicitud.MismoCodigo"/>. El envío la registra con el <c>solicitudNexoId</c>
    /// (<see cref="clsEnvioNexoRegistrado"/>) y el polling compara contra ella: sin esto, el emparejamiento por contenido no reconocería la
    /// línea (<c>C2</c> contra <c>2</c>) y la regla "la Mesa manda" (D2) tomaría el <c>2</c> de Nexo como un cambio de la Mesa (y borraría
    /// los porcentajes de llenado del cubicador).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> EquivalenciasVehiculo(
        clsSolicitudNexo solicitud, ModoCodigosCubicador modo, IReadOnlyCollection<string>? vehiculosNexo,
        Aplicacion.CatalogosFlete.DTOs.clsDiccionarioAliasNexo? alias)
    {
        var equivalencias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var linea in solicitud.Lineas)
        {
            if (string.IsNullOrWhiteSpace(linea.TipoVehiculo)
                || VehiculoLinea(linea, modo, vehiculosNexo, alias).TipoVehiculo is not { } enviado
                || clsVehiculoLineaSolicitud.MismoCodigo(linea.TipoVehiculo, enviado))
            {
                continue;
            }

            equivalencias.TryAdd(linea.TipoVehiculo.Trim(), enviado);
        }

        return equivalencias;
    }

    /// <summary>
    /// Deja en el log los códigos del cubicador que ni el catálogo ni el alias de Nexo conocen: con <c>solo_conocidos</c> su línea viajó sin
    /// vehículo; con <c>siempre</c>, tal cual (Nexo la pone en cuarentena para normalizarla en su bandeja, NEXO #322). Y los que resolvió
    /// el alias.
    /// </summary>
    private void AvisarCodigosDesconocidos(
        clsSolicitudNexo solicitud, IReadOnlyCollection<string>? vehiculosNexo, Aplicacion.CatalogosFlete.DTOs.clsDiccionarioAliasNexo? alias)
    {
        if (_modoCodigosCubicador == ModoCodigosCubicador.Nunca)
        {
            return;
        }

        var resoluciones = solicitud.Lineas
            .Where(l => l.IdRecomendacionVehiculo is not null && !string.IsNullOrWhiteSpace(l.TipoVehiculo))
            .Select(l => (Codigo: l.TipoVehiculo!.Trim(), Resuelto: Aplicacion.CatalogosFlete.Services.clsResolucionCodigosNexo.Vehiculo(vehiculosNexo, alias, l.TipoVehiculo)))
            .ToList();
        var desconocidos = resoluciones.Where(r => r.Resuelto is null).Select(r => r.Codigo).ToList();
        if (desconocidos.Count > 0)
        {
            var codigos = string.Join(", ", desconocidos.Distinct(StringComparer.Ordinal));
            if (_modoCodigosCubicador == ModoCodigosCubicador.Siempre)
            {
                logger.LogInformation(
                    "{Lineas} línea(s) de camiones del cubicador viajan a Nexo con un código que ni su catálogo ni su diccionario de alias conocen ({Codigos}; Nexo:EnviarCodigosCubicador = siempre): Nexo las pone en cuarentena.",
                    desconocidos.Count, codigos);
            }
            else
            {
                logger.LogInformation(
                    "{Lineas} línea(s) de camiones del cubicador viajan a Nexo sin vehículo: ni su catálogo ni su diccionario de alias conocen el código ({Codigos}; Nexo:EnviarCodigosCubicador = solo_conocidos).",
                    desconocidos.Count, codigos);
            }
        }

        var porAlias = resoluciones.Where(r => r.Resuelto?.Fuente == Aplicacion.CatalogosFlete.Services.FuenteCodigoNexo.Alias)
            .Select(r => $"{r.Codigo} → {r.Resuelto!.Codigo}").Distinct(StringComparer.Ordinal).ToList();
        if (porAlias.Count > 0)
        {
            logger.LogInformation("Códigos del cubicador resueltos con el diccionario de alias de Nexo: {Alias}.", string.Join(", ", porAlias));
        }
    }

    /// <summary>
    /// HU-031.1 (#194): el diccionario de alias de Nexo, solo si alguna línea que viaja con vehículo trae un código que el catálogo oficial
    /// no reconoce. <c>null</c> si no hace falta o no está disponible (Nexo caído y sin copia, Nexo sin #321): el envío NUNCA falla por
    /// esto; sigue con el modo configurado sin alias y deja un warning.
    /// </summary>
    private async Task<Aplicacion.CatalogosFlete.DTOs.clsDiccionarioAliasNexo?> AliasAsync(
        Guid usuarioId, clsSolicitudNexo solicitud, IReadOnlyCollection<string>? vehiculosNexo, CancellationToken cancellationToken)
    {
        if (alias is null || !solicitud.Lineas.Any(l => !string.IsNullOrWhiteSpace(l.TipoVehiculo)
                && (l.IdRecomendacionVehiculo is null || _modoCodigosCubicador != ModoCodigosCubicador.Nunca)
                && clsCodigosConocidosNexo.Resolver(vehiculosNexo, l.TipoVehiculo) is null))
        {
            return null;
        }

        try
        {
            var resultado = await alias.ObtenerAsync(usuarioId, cancellationToken);
            if (resultado.EsExitoso)
            {
                return resultado.Valor;
            }

            logger.LogWarning(
                "Sin el diccionario de alias de Nexo ({Codigo}): los códigos que el catálogo no conoce siguen sin alias (Nexo:EnviarCodigosCubicador = {Modo}).",
                resultado.Error.Codigo, _modoCodigosCubicador);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Falló la lectura del diccionario de alias de Nexo ({Tipo}): se envía sin alias.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Los códigos de <c>GET /api/catalogos/tipos-vehiculo</c> de Nexo (la copia en caché: vigente <c>Nexo:MinutosCacheCatalogos</c> y, si
    /// Nexo no responde, la vencida hasta <c>Nexo:HorasCatalogosVencidos</c>), solo si alguna línea trae vehículo. <c>null</c> si no hace
    /// falta o no hay catálogo: entonces ningún código del cubicador es "conocido" y su línea va sin vehículo (con <c>solo_conocidos</c>),
    /// lo que nunca genera cuarentena. Nunca falla el envío por el catálogo.
    /// </summary>
    private async Task<IReadOnlyCollection<string>?> VehiculosNexoAsync(Guid usuarioId, clsSolicitudNexo solicitud, CancellationToken cancellationToken)
    {
        if (catalogos is null || !solicitud.Lineas.Any(l => !string.IsNullOrWhiteSpace(l.TipoVehiculo)
                && (l.IdRecomendacionVehiculo is null || _modoCodigosCubicador != ModoCodigosCubicador.Nunca)))
        {
            return null;
        }

        try
        {
            var resultado = await catalogos.ObtenerAsync(usuarioId, cancellationToken);
            if (resultado.EsExitoso)
            {
                return [.. resultado.Valor.TiposVehiculo.Select(t => t.Valor)];
            }

            logger.LogWarning(
                "Sin el catálogo de tipos de vehículo de Nexo ({Codigo}): ningún código es conocido; con Nexo:EnviarCodigosCubicador = {Modo}, los camiones del cubicador de solo_conocidos viajan sin vehículo.",
                resultado.Error.Codigo, _modoCodigosCubicador);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Falló la lectura del catálogo de tipos de vehículo de Nexo ({Tipo}): ningún código es conocido.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Envía la petición con la identidad del comercial y traduce los fallos. Devuelve el JSON de una respuesta 2xx (el llamador lo
    /// libera) o el error de negocio. Con <paramref name="limiteBytes"/>, una respuesta 2xx más grande se descarta sin leerla entera
    /// (<c>COSTEO_NEXO_NO_DISPONIBLE</c>, "respuesta demasiado grande"); lo usan los servicios conexos (HU-013.3).
    /// </summary>
    internal async Task<clsResultado<JsonDocument>> EnviarAsync(
        Guid usuarioId, HttpMethod metodo, string ruta, object? cuerpo, CancellationToken cancellationToken, long? limiteBytes = null,
        bool vacioEsNulo = false)
    {
        if (_urlBase is null)
        {
            return NoDisponible("Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        using var peticion = new HttpRequestMessage(metodo, ConstruirUrl(_urlBase, ruta));
        if (cuerpo is not null)
        {
            peticion.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: _json);
        }

        clsIdentidadNexo.Identificar(peticion, idUsuarioExterno);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // HU-023.4 (#159): cupo de Nexo agotado (por rol). No es un rechazo de los datos: no disponible, reintentable cuando pase el
                // Retry-After (clsCupoNexoHandler ya bloqueó las lecturas hasta entonces).
                var (_, _, detalle429) = await LeerErrorNexoAsync(respuesta, cancellationToken);
                return NoDisponible(ConEspera(detalle429, respuesta));
            }

            if ((int)respuesta.StatusCode is >= 400 and < 500)
            {
                var (codigoNexo, mensajeNexo, detalle) = await LeerErrorNexoAsync(respuesta, cancellationToken);
                logger.LogWarning("Nexo rechazó {Metodo} {Ruta}: {Detalle}", metodo, ruta, detalle);

                // Conflictos de estado de Nexo que el llamador distingue (HU-005.14): viajan con el código de Nexo.
                if (respuesta.StatusCode == HttpStatusCode.Conflict
                    && codigoNexo is clsCodigosError.ResultadoYaDefinido or clsCodigosError.CotizacionNoEntregada or clsCodigosError.SolicitudNoTransicionable)
                {
                    return new clsErrorDominio(codigoNexo, mensajeNexo ?? MensajeRechazada, detalle);
                }

                if (respuesta.StatusCode == HttpStatusCode.NotFound && codigoNexo == clsCodigosError.CotizacionNoEncontrada)
                {
                    return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, mensajeNexo ?? "Cotización no encontrada en Nexo.", detalle);
                }

                // 401/403: la identidad del comercial no vale en Nexo (sin idUsuarioExterno válido o sin rol en Cabina de Fletes). Es
                // configuración, no un rechazo de los datos: no disponible (503), con una línea de error clara.
                if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    logger.LogError("Nexo respondió {Estado} a {Metodo} {Ruta}: revisar la identidad X-Portal-clsUsuario-Id del comercial y su rol en Cabina de Fletes ({Detalle}).",
                        (int)respuesta.StatusCode, metodo, ruta, detalle);
                    return new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, MensajeNoDisponible, detalle);
                }

                return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, MensajeRechazada, detalle);
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            if (vacioEsNulo && respuesta.StatusCode == HttpStatusCode.NoContent)
            {
                // GET /api/cotizaciones/por-solicitud/{id} sin cotización: un JSON null para el llamador.
                return JsonDocument.Parse("null");
            }

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            if (limiteBytes is not { } limite)
            {
                return await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);
            }

            if (respuesta.Content.Headers.ContentLength is { } largo && largo > limite)
            {
                return NoDisponible($"respuesta demasiado grande ({largo.ToString(CultureInfo.InvariantCulture)} bytes)");
            }

            using var acotado = await LeerAcotadoAsync(flujo, limite, cancellationToken);
            return acotado is null
                ? NoDisponible($"respuesta de más de {limite.ToString(CultureInfo.InvariantCulture)} bytes")
                : await JsonDocument.ParseAsync(acotado, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de Nexo
        }
        catch (Exception ex) when (ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            logger.LogWarning("Nexo no respondió a tiempo en {Metodo} {Ruta}.", metodo, ruta);
            logger.LogDebug(ex, "Detalle del tiempo agotado en Nexo ({Ruta}).", ruta);
            return new clsErrorDominio(clsCodigosError.CosteoNexoTiempoAgotado, MensajeTiempoAgotado);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            return NoDisponible(ex is Polly.CircuitBreaker.BrokenCircuitException ? "circuito abierto tras fallos repetidos" : "error de red", ex);
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary>
    /// POST con efecto en Nexo (agregar conexos, HU-013.3) que distingue si el efecto pudo ocurrir: nunca se cancela a mitad (sin
    /// token del llamador) y nunca se reintenta. <see cref="FasePostNexo.NoEnviado"/> (sin configurar, comercial sin
    /// <c>idUsuarioExterno</c>, circuito abierto): sin efecto. <see cref="FasePostNexo.Rechazado"/> (4xx): sin efecto, con el detalle
    /// <c>HTTP {estado} {código}: {mensaje}</c>. <see cref="FasePostNexo.Exito"/> (cualquier 2xx): CON efecto, con el JSON solo si es un
    /// objeto válido de hasta <paramref name="limiteBytes"/> (204, vacío, inválido o más grande: <c>null</c>).
    /// <see cref="FasePostNexo.Incierto"/> (tiempo agotado, error de red, 5xx): el efecto no se puede descartar.
    /// </summary>
    internal async Task<clsRespuestaPostNexo> EnviarPostConEfectoAsync(Guid usuarioId, string ruta, object cuerpo, long limiteBytes)
    {
        if (_urlBase is null)
        {
            return new clsRespuestaPostNexo(FasePostNexo.NoEnviado, null, "Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, CancellationToken.None);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return new clsRespuestaPostNexo(FasePostNexo.NoEnviado, null, usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno");
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Post, ConstruirUrl(_urlBase, ruta))
        {
            Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: _json),
        };
        clsIdentidadNexo.Identificar(peticion, idUsuarioExterno);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            return new clsRespuestaPostNexo(FasePostNexo.NoEnviado, null, "circuito abierto tras fallos repetidos");
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or ExecutionRejectedException)
        {
            logger.LogWarning("POST {Ruta} a Nexo sin respuesta ({Tipo}): el efecto es incierto.", ruta, ex.GetType().Name);
            return new clsRespuestaPostNexo(FasePostNexo.Incierto, null, ex is HttpRequestException ? "error de red" : "Nexo no respondió a tiempo");
        }

        using (respuesta)
        {
            var estado = (int)respuesta.StatusCode;
            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Un 429 no aplicó nada: equivale a no haber enviado (reintentable), nunca a un rechazo de los datos (HU-023.4).
                var (_, _, detalle429) = await LeerErrorNexoAsync(respuesta, CancellationToken.None);
                logger.LogWarning("Nexo respondió 429 a POST {Ruta}: {Detalle}", ruta, detalle429);
                return new clsRespuestaPostNexo(FasePostNexo.NoEnviado, null, ConEspera(detalle429, respuesta));
            }

            if (estado is >= 400 and < 500)
            {
                var (_, _, detalle) = await LeerErrorNexoAsync(respuesta, CancellationToken.None);
                logger.LogWarning("Nexo rechazó POST {Ruta}: {Detalle}", ruta, detalle);
                return new clsRespuestaPostNexo(FasePostNexo.Rechazado, null, detalle);
            }

            if (estado is < 200 or >= 300)
            {
                logger.LogWarning("Nexo respondió {Estado} a POST {Ruta}: el efecto es incierto.", estado, ruta);
                return new clsRespuestaPostNexo(FasePostNexo.Incierto, null, $"HTTP {estado.ToString(CultureInfo.InvariantCulture)}");
            }

            try
            {
                if (respuesta.Content.Headers.ContentLength is { } largo && largo > limiteBytes)
                {
                    return new clsRespuestaPostNexo(FasePostNexo.Exito, null, "respuesta demasiado grande");
                }

                await using var flujo = await respuesta.Content.ReadAsStreamAsync(CancellationToken.None);
                using var acotado = await LeerAcotadoAsync(flujo, limiteBytes, CancellationToken.None);
                if (acotado is null || acotado.Length == 0)
                {
                    return new clsRespuestaPostNexo(FasePostNexo.Exito, null, acotado is null ? "respuesta demasiado grande" : "respuesta vacía");
                }

                var documento = await JsonDocument.ParseAsync(acotado, cancellationToken: CancellationToken.None);
                return new clsRespuestaPostNexo(FasePostNexo.Exito, documento, null);
            }
            catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or OperationCanceledException)
            {
                // El 2xx ya llegó: el efecto ocurrió aunque el cuerpo no se pueda leer.
                logger.LogWarning("Nexo respondió {Estado} a POST {Ruta} con un cuerpo ilegible ({Tipo}).", estado, ruta, ex.GetType().Name);
                return new clsRespuestaPostNexo(FasePostNexo.Exito, null, "respuesta ilegible");
            }
        }
    }

    /// <summary>Lee hasta <paramref name="limite"/> bytes en memoria; <c>null</c> si el flujo trae más.</summary>
    private static async Task<MemoryStream?> LeerAcotadoAsync(Stream flujo, long limite, CancellationToken cancellationToken)
    {
        var memoria = new MemoryStream();
        var bufer = new byte[16 * 1024];
        int leidos;
        while ((leidos = await flujo.ReadAsync(bufer, cancellationToken)) > 0)
        {
            if (memoria.Length + leidos > limite)
            {
                await memoria.DisposeAsync();
                return null;
            }

            memoria.Write(bufer, 0, leidos);
        }

        memoria.Position = 0;
        return memoria;
    }

    /// <summary>
    /// El detalle de un 429 con la espera del <c>Retry-After</c> (segundos o fecha; una fecha pasada es 0 s), para el log y el motivo del
    /// error.
    /// </summary>
    internal static string ConEspera(string detalle, HttpResponseMessage respuesta) =>
        Comun.clsCupoNexo.EsperaDe(respuesta, DateTimeOffset.UtcNow) is { } espera
            ? $"{detalle} (Retry-After {((int)Math.Ceiling(espera.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} s)"
            : detalle;

    /// <summary>Error de Nexo (<c>{code, message, timestamp, details?}</c>) como <c>HTTP {estado} {code}: {message}</c> (máx. 400 caracteres).</summary>
    private static async Task<(string? Codigo, string? Mensaje, string Detalle)> LeerErrorNexoAsync(HttpResponseMessage respuesta, CancellationToken cancellationToken)
    {
        var estado = (int)respuesta.StatusCode;
        var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken);
        string? codigo = null;
        string? mensaje = null;
        try
        {
            using var documento = JsonDocument.Parse(texto);
            if (documento.RootElement.ValueKind == JsonValueKind.Object)
            {
                codigo = Texto(documento.RootElement, "code");
                mensaje = Texto(documento.RootElement, "message");
            }
        }
        catch (JsonException)
        {
            // cuerpo que no es JSON: se usa el texto recortado
        }

        var explicacion = mensaje ?? (string.IsNullOrWhiteSpace(texto) ? null : texto.Trim());
        var detalle = $"HTTP {estado.ToString(CultureInfo.InvariantCulture)}"
            + (codigo is null ? string.Empty : $" {codigo}")
            + (explicacion is null ? string.Empty : $": {explicacion}");
        return (codigo, mensaje, detalle.Length <= 400 ? detalle : detalle[..399] + "…");
    }

    internal static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor)
            ? valor.ValueKind switch
            {
                JsonValueKind.String => valor.GetString(),
                JsonValueKind.Number => valor.GetRawText(),
                _ => null,
            }
            : null;

    internal static int? Entero(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.Number
            && valor.TryGetInt32(out var numero)
            ? numero
            : null;

    internal static decimal? Decimal(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.Number
            && valor.TryGetDecimal(out var numero)
            ? numero
            : null;

    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Costeo en Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del costeo en Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, MensajeNoDisponible, motivo);
    }

    // --- Forma exacta de CapturaSolicitudRequest / CapturaLineaRequest / CapturaPosicionRequest de Nexo (camelCase) ---

    private sealed record clsCapturaSolicitudWire(
        string? ClienteBp, Guid? ClienteId, string Tipo, DateTimeOffset? Deadline,
        decimal? HorasCargue, decimal? HorasDescargue, string? Notas, IReadOnlyList<clsCapturaLineaWire> Lineas);

    private sealed record clsCapturaLineaWire(
        string Origen, string Destino, int? OrigenMunicipioId, int? DestinoMunicipioId, string? TipoVehiculo, string? Carroceria,
        string? Tipologia, string? LineaNegocio, string? ClaseViaje, string? ClaseContenedor, bool? Exclusivo, bool? Consolidado,
        bool? RetornaContenedor, string? CiudadDevolucionContenedor, bool? LevanteContenedorDiferente, string? CiudadLevanteContenedor,
        decimal? ValorAprovisionamiento, decimal? HorasCargue, decimal? HorasDescargue, decimal? Peso, decimal? ValorMercancia,
        IReadOnlyList<clsCapturaPosicionWire>? Posiciones, string? GrupoRutaId, string? IdRecomendacionVehiculo);

    private sealed record clsResultadoWire(string Resultado, string? Motivo, string? Comentario);

    /// <summary><c>clsRechazarSolicitudRequest</c> de Nexo (HU-028): <c>motivo</c> (1-100) y <c>comentario</c> opcional (hasta 500).</summary>
    private sealed record clsRechazoWire(string Motivo, string? Comentario);

    private sealed record clsCapturaPosicionWire(string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);
}

/// <summary>Qué se sabe del efecto de un POST a Nexo (ver <see cref="clsCosteoNexo.EnviarPostConEfectoAsync"/>).</summary>
internal enum FasePostNexo
{
    /// <summary>No salió de Cotifly: sin efecto.</summary>
    NoEnviado,

    /// <summary>Nexo respondió 4xx: sin efecto.</summary>
    Rechazado,

    /// <summary>Nexo respondió 2xx: con efecto (el cuerpo puede faltar).</summary>
    Exito,

    /// <summary>Tiempo agotado, error de red o 5xx: el efecto no se puede descartar.</summary>
    Incierto,
}

/// <summary>Resultado de un POST con efecto: la fase, el JSON de un 2xx legible (lo libera quien llama) y el detalle técnico.</summary>
internal sealed record clsRespuestaPostNexo(FasePostNexo Fase, JsonDocument? Json, string? Detalle);
