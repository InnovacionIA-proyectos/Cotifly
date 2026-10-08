using System.Text.Json.Serialization;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun;
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
        clsCodigosError.UsarResultadoNexo,
        clsCodigosError.SolicitudNoTransicionable,
        clsCodigosError.ConflictoConcurrencia,
        clsCodigosError.PdfYaEnviado,
        clsCodigosError.LineaSinCotizacionNexo,
        clsCodigosError.EstadoNoPermite,
    };

    public static IActionResult Error(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Codigo switch
        {
            clsCodigosError.CotizacionNoEncontrada => new NotFoundObjectResult(new clsErrorWeb(MensajeNoEsTuya)),
            clsCodigosAplicacion.NoEncontrado => new NotFoundObjectResult(new clsErrorWeb(error.Mensaje)),
            clsCodigosAplicacion.NoAutenticado or clsTelefonoComercialService.CodigoUsuarioNoEncontrado =>
                new UnauthorizedObjectResult(new clsErrorWeb(clsCodigosAcceso.MensajeSesionExpirada)),
            clsCodigosAplicacion.SinPermiso => new ObjectResult(new clsErrorWeb(clsErrorWeb.MensajeSinPermiso)) { StatusCode = StatusCodes.Status403Forbidden },
            clsCodigosError.CorreoDeshabilitado or clsCodigosError.CosteoNexoNoDisponible or clsCodigosError.CosteoNexoTiempoAgotado
                or clsCodigosAplicacion.RegionalNoDisponible or clsCodigosError.CatalogosFleteNoDisponible or clsCodigosError.NexoNoDisponible
                or clsCodigosError.MunicipiosNoDisponible or clsCodigosError.PdfRenderNoDisponible =>
                new ObjectResult(clsErrorNegocioWeb.Desde(error)) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            clsCodigosAplicacion.RegionalNoAsignada => new ConflictObjectResult(clsErrorNegocioWeb.Desde(error)),
            clsCodigosError.NexoSinPermiso => new ObjectResult(clsErrorNegocioWeb.Desde(error)) { StatusCode = StatusCodes.Status403Forbidden },
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
