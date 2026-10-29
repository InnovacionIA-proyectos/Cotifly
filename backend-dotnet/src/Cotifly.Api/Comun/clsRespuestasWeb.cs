using System.Text.Json.Serialization;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cotifly.Api.Comun;

/// <summary>
/// <c>ErrorNegocio</c> del contrato: <c>{"error": {"codigo", "mensaje", "detalle?"}}</c>. <c>detalle</c> es un objeto en el
/// contrato: los datos estructurados del error (<see cref="clsErrorDominio.Datos"/>, ej. <c>margen_sugerido</c>) y el texto de
/// <see cref="clsErrorDominio.Detalle"/> como <c>motivo</c>; se omite si no hay ninguno.
/// </summary>
public sealed record clsErrorNegocioWeb([property: JsonPropertyName("error")] clsErrorNegocioWeb.clsCuerpo Error)
{
    public static clsErrorNegocioWeb Desde(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Dictionary<string, object?>? detalle = error.Datos is { Count: > 0 } datos ? new(datos, StringComparer.Ordinal) : null;
        if (!string.IsNullOrWhiteSpace(error.Detalle))
        {
            detalle ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            detalle["motivo"] = error.Detalle;
        }

        return new clsErrorNegocioWeb(new clsCuerpo(error.Codigo, error.Mensaje, detalle));
    }

    public sealed record clsCuerpo(
        [property: JsonPropertyName("codigo")] string Codigo,
        [property: JsonPropertyName("mensaje")] string Mensaje,
        [property: JsonPropertyName("detalle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Detalle);
}

/// <summary>
/// Traducción única de los errores de los casos de uso a HTTP en <c>/web/*</c> (tabla "Para #12" de docs/07-api.md, §7.4, y
/// <c>specs/openapi.yaml</c>). Un 404 nunca distingue "no existe" de "no es tuya".
/// </summary>
public static class clsRespuestasWeb
{
    public const string MensajeNoEsTuya = "Cotización no encontrada o no es tuya.";

    /// <summary>Segundos del encabezado <c>Retry-After</c> del 503 <c>SERVICIO_OCUPADO</c> (#125).</summary>
    public const int SegundosReintentoServicioOcupado = 1;

    /// <summary>Opción C: segundos del encabezado <c>Retry-After</c> del 503 <c>CUBICADOR_OCUPADO</c> (el cubicador calcula otra carga).</summary>
    public const int SegundosReintentoCubicadorOcupado = 10;

    /// <summary>Códigos que responden 400 con <c>ErrorWeb</c> (validación); el resto de errores de negocio va como <c>ErrorNegocio</c>.</summary>
    private static readonly HashSet<string> _validacion = new(StringComparer.Ordinal)
    {
        clsCodigosAplicacion.SolicitudInvalida,
        clsTelefonoComercialService.CodigoTelefonoInvalido,
        clsCorreo.CodigoError,
        clsSeguimientoService.CodigoSeguimientoInvalido,
    };

    /// <summary>Códigos que responden 409 con <c>ErrorNegocio</c> (el estado de la cotización no permite la operación, ADR-015).</summary>
    private static readonly HashSet<string> _conflicto = new(StringComparer.Ordinal)
    {
        clsCodigosError.CosteoNoReintentable,
        clsCodigosError.CosteoNoListo,
        clsCodigosError.PdfYaGenerado,
        clsCodigosError.TextosEditados,
        clsCodigosError.ResultadoYaDefinido,
        clsCodigosError.CotizacionNoEntregada,
        // Endurecimiento B (#199): Nexo no reconoce la cotización de Nexo del folio (no es el 404 "no es tuya").
        clsCodigosError.CotizacionNexoNoEncontrada,
        clsCodigosError.UsarResultadoNexo,
        clsCodigosError.SolicitudNoTransicionable,
        clsCodigosError.ConflictoConcurrencia,
        clsCodigosError.PdfYaEnviado,
        clsCodigosError.LineaSinCotizacionNexo,
        clsCodigosError.EstadoNoPermite,
        // HU-028.1 (#183): ruta rechazada por la Mesa de Costeo (margen o conexos) y PDF con todas las rutas rechazadas.
        clsCodigosError.RutaRechazada,
        clsCodigosError.SinRutasCotizables,
        // HU-033 (#199): el envío no se puede reintentar en MailSender (no está fallido o MailSender no lo conoce).
        clsCodigosError.CorreoNoReintentable,
        // HU-036.2 (#206): finalizar una cotización sin el costeo de Nexo listo.
        clsCodigosError.CotizacionNoFinalizable,
        // HU-036.3 (#207): PDF, correo, resultado y textos exigen la cotización finalizada.
        clsCodigosError.CotizacionNoFinalizada,
        // D4 (E2E 2026-10-16): la misma Idempotency-Key con otro cuerpo (antes 400 ErrorWeb).
        clsCodigosAplicacion.IdempotencyKeyReutilizada,
    };

    public static IActionResult Error(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Codigo switch
        {
            // #125: reintentable de inmediato; Retry-After le dice al cliente cuándo.
            clsCodigosError.ServicioOcupado =>
                new clsResultadoReintentable(clsErrorNegocioWeb.Desde(error), SegundosReintentoServicioOcupado) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            clsCodigosError.CubicadorOcupado =>
                new clsResultadoReintentable(clsErrorNegocioWeb.Desde(error), SegundosReintentoCubicadorOcupado) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            clsCodigosError.CotizacionNoEncontrada => new NotFoundObjectResult(new clsErrorWeb(MensajeNoEsTuya)),
            clsCodigosAplicacion.NoEncontrado => new NotFoundObjectResult(new clsErrorWeb(error.Mensaje)),
            clsCodigosAplicacion.NoAutenticado or clsTelefonoComercialService.CodigoUsuarioNoEncontrado =>
                new UnauthorizedObjectResult(new clsErrorWeb(clsCodigosAcceso.MensajeSesionExpirada)),
            clsCodigosAplicacion.SinPermiso => new ObjectResult(new clsErrorWeb(clsErrorWeb.MensajeSinPermiso)) { StatusCode = StatusCodes.Status403Forbidden },
            clsCodigosError.CorreoDeshabilitado or clsCodigosError.CosteoNexoNoDisponible or clsCodigosError.CosteoNexoTiempoAgotado
                or clsCodigosAplicacion.RegionalNoDisponible or clsCodigosError.CatalogosFleteNoDisponible or clsCodigosError.NexoNoDisponible
                or clsCodigosError.MunicipiosNoDisponible or clsCodigosError.PdfRenderNoDisponible
                // OBS-1021B-3 (E2E 2026-10-21): la búsqueda de clientes sin Nexo es 503, como los demás catálogos (antes 400).
                or clsCodigosError.ClienteBusquedaNoDisponible
                or clsCodigosError.CubicadorNoDisponible or clsCodigosError.CubicadorRespuestaInvalida or clsCodigosError.CubicadorNoAutorizado =>
                new ObjectResult(clsErrorNegocioWeb.Desde(error)) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            clsCodigosAplicacion.RegionalNoAsignada => new ConflictObjectResult(clsErrorNegocioWeb.Desde(error)),
            clsCodigosError.NexoSinPermiso or clsCodigosAplicacion.RegionalNoPermitida =>
                new ObjectResult(clsErrorNegocioWeb.Desde(error)) { StatusCode = StatusCodes.Status403Forbidden },
            // Endurecimiento A (#199): un error interno es 500, nunca 400; REGISTRO_DUPLICADO (una restricción única que el caso de uso no
            // tradujo) también, sin el nombre técnico de la restricción: el detalle queda solo en el log.
            clsCodigosError.ErrorInterno or clsViolacionUnicidadException.CodigoRegistroDuplicado => new clsResultadoErrorInterno(error),
            clsCodigosError.PrecondicionRequerida =>
                new ObjectResult(clsErrorNegocioWeb.Desde(error)) { StatusCode = StatusCodes.Status428PreconditionRequired },
            _ when _conflicto.Contains(error.Codigo) => new ConflictObjectResult(clsErrorNegocioWeb.Desde(error)),
            _ when _validacion.Contains(error.Codigo) => new BadRequestObjectResult(new clsErrorWeb(error.Mensaje)),
            _ => new BadRequestObjectResult(clsErrorNegocioWeb.Desde(error)),
        };
    }

    /// <summary>404 <c>NoEsTuya</c> (ids o folios que ni siquiera tienen forma válida).</summary>
    public static IActionResult NoEsTuya() => new NotFoundObjectResult(new clsErrorWeb(MensajeNoEsTuya));
}

/// <summary>
/// Endurecimiento A (#199): 500 <c>ErrorNegocio</c> <c>ERROR_INTERNO</c> con el mensaje genérico de
/// <see cref="clsManejadorErroresNoControlados"/>, para un error de dominio que no es del cliente (<c>ERROR_INTERNO</c>,
/// <c>REGISTRO_DUPLICADO</c>). El código, el mensaje y el detalle originales (por ejemplo, el nombre de la restricción) van solo al log, con
/// el id de correlación de la petición.
/// </summary>
public sealed partial class clsResultadoErrorInterno : ObjectResult
{
    public clsResultadoErrorInterno(clsErrorDominio original)
        : base(clsManejadorErroresNoControlados.CuerpoErrorInterno(idCorrelacion: null))
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        StatusCode = StatusCodes.Status500InternalServerError;
    }

    /// <summary>El error de dominio tal como llegó (solo para el log; nunca sale en la respuesta).</summary>
    public clsErrorDominio Original { get; }

    public override Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Value = clsManejadorErroresNoControlados.CuerpoErrorInterno(context.HttpContext.TraceIdentifier);
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(clsResultadoErrorInterno).FullName!);
        LogErrorInterno(logger, Original.Codigo, Original.Mensaje, Original.Detalle, context.HttpContext.TraceIdentifier,
            context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value ?? string.Empty);
        return base.ExecuteResultAsync(context);
    }

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error,
        Message = "Error interno {Codigo} en {Metodo} {Ruta} (id de correlación {IdCorrelacion}): {Mensaje} {Detalle}. Se respondió 500 ERROR_INTERNO.")]
    private static partial void LogErrorInterno(ILogger logger, string codigo, string mensaje, string? detalle, string idCorrelacion, string metodo, string ruta);
}

/// <summary><see cref="ObjectResult"/> que agrega <c>Retry-After</c> (segundos) a la respuesta (#125).</summary>
public sealed class clsResultadoReintentable(object? valor, int segundos) : ObjectResult(valor)
{
    public int Segundos { get; } = segundos;

    public override void OnFormatting(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.OnFormatting(context);
        context.HttpContext.Response.Headers.RetryAfter = Segundos.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Errores de cliente que genera MVC sin pasar por un caso de uso (415 sin <c>Content-Type</c> JSON, 404/405 de acciones...):
/// siempre <c>ErrorWeb</c> (<c>{"error": "..."}</c>, contrato #17), nunca <c>ProblemDetails</c>.
/// </summary>
public sealed class clsFabricaErroresCliente : IClientErrorFactory
{
    public const string MensajeTipoContenido = "Cuerpo inválido: envía JSON con Content-Type: application/json.";

    public IActionResult GetClientError(ActionContext actionContext, IClientErrorActionResult clientError)
    {
        ArgumentNullException.ThrowIfNull(clientError);
        var estado = clientError.StatusCode ?? StatusCodes.Status400BadRequest;
        var mensaje = estado switch
        {
            StatusCodes.Status401Unauthorized => clsCodigosAcceso.MensajeSesionExpirada,
            StatusCodes.Status403Forbidden => clsErrorWeb.MensajeSinPermiso,
            StatusCodes.Status404NotFound => "Recurso no encontrado.",
            StatusCodes.Status415UnsupportedMediaType => MensajeTipoContenido,
            _ => clsErrorWeb.MensajeCuerpoInvalido,
        };
        return new ObjectResult(new clsErrorWeb(mensaje)) { StatusCode = estado };
    }

    /// <summary>Reemplaza la fábrica de <c>ProblemDetails</c> que registra MVC.</summary>
    public static IServiceCollection Registrar(IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        servicios.Replace(ServiceDescriptor.Singleton<IClientErrorFactory, clsFabricaErroresCliente>());
        return servicios;
    }
}

/// <summary>
/// Base de los controllers de <c>/web/*</c>: <c>[ApiController]</c> (400 con <c>ErrorWeb</c>), JSON snake_case
/// (<see cref="clsJsonSnakeCaseAttribute"/>) y política <see cref="clsPoliticas.Comercial"/> (comercial o gerente) salvo que la
/// acción pida otra más estricta. Los controllers no tienen lógica de negocio: delegan en los casos de uso.
/// </summary>
[ApiController]
[clsJsonSnakeCase]
[Authorize(Policy = clsPoliticas.Comercial)]
[Produces("application/json")]
public abstract class clsControladorWebBase(IClsContextoUsuario usuario) : ControllerBase
{
    /// <summary>Usuario del token (la política garantiza que existe; <see cref="Guid.Empty"/> hace fallar al caso de uso).</summary>
    protected Guid UsuarioId => usuario.UsuarioId ?? Guid.Empty;

    protected static IActionResult Error(clsErrorDominio error) => clsRespuestasWeb.Error(error);
}
