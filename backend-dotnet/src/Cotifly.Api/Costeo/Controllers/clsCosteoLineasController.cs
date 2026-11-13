using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Costeo.Controllers;

/// <summary>
/// Margen y servicios conexos para varias rutas (o todas) en UNA operación atómica (ADR-026, <c>specs/openapi.yaml</c>). Las
/// mismas reglas que los endpoints por ruta; si una ruta no admite el cambio no cambia ninguna y el error trae su <c>detalle.linea_orden</c>.
/// <c>If-Match</c> obligatorio e <c>Idempotency-Key</c> opcional. Solo cotizaciones propias; una ajena responde 404 igual que una inexistente.
/// </summary>
[Route("web")]
public sealed class clsCosteoLineasController(IClsContextoUsuario usuario, IClsCosteoLineasService lineas) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>CosteoCotizacion</c> con el margen aplicado (o el sugerido) en las rutas elegidas (y el <c>ETag</c> nuevo); 400 <c>ErrorWeb</c>
    /// (forma), <c>AJUSTE_MARGEN_INVALIDO</c>, <c>JUSTIFICACION_REQUERIDA</c> o <c>RUTA_INEXISTENTE</c>; 404; 409 <c>COSTEO_NO_LISTO</c>,
    /// <c>RUTA_RECHAZADA</c>, <c>SIN_RUTAS_COTIZABLES</c>, <c>PDF_YA_ENVIADO</c>, <c>CONFLICTO_CONCURRENCIA</c> o
    /// <c>IDEMPOTENCY_KEY_REUTILIZADA</c>; 428 sin <c>If-Match</c>.
    /// </summary>
    [HttpPut("cotizaciones/{folio}/costeo/margen/lineas")]
    [clsRequiereIfMatch]
    [ProducesResponseType<Aplicacion.Costeo.Services.clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> AjustarMargen(string folio, [FromBody] clsMargenLineasWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await lineas.AjustarMargenAsync(
            UsuarioId, folio, cuerpo.AEntrada(), clsConcurrenciaHttp.VersionDe(HttpContext), Clave(), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 200 <c>CosteoCotizacion</c> con el conexo agregado a las rutas elegidas (<c>estado_nexo = pendiente</c>: el envío diferido lo lleva a
    /// Nexo); 400 <c>ErrorWeb</c> (forma), <c>CONEXO_INVALIDO</c> o <c>RUTA_INEXISTENTE</c>; 403 <c>NEXO_SIN_PERMISO</c>; 404; 409
    /// <c>COSTEO_NO_LISTO</c>, <c>RUTA_RECHAZADA</c>, <c>LINEA_SIN_COTIZACION_NEXO</c>, <c>ESTADO_NO_PERMITE</c> (licitación),
    /// <c>SIN_RUTAS_COTIZABLES</c>, <c>PDF_YA_ENVIADO</c>, <c>CONFLICTO_CONCURRENCIA</c> o <c>IDEMPOTENCY_KEY_REUTILIZADA</c>; 428; 503
    /// <c>NEXO_NO_DISPONIBLE</c> (catálogo de conexos; nada cambia).
    /// </summary>
    [HttpPost("cotizaciones/{folio}/costeo/servicios-conexos/lineas")]
    [clsRequiereIfMatch]
    [ProducesResponseType<Aplicacion.Costeo.Services.clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AgregarConexo(string folio, [FromBody] clsConexoLineasWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await lineas.AgregarConexoAsync(
            UsuarioId, folio, cuerpo.AEntrada(), clsConcurrenciaHttp.VersionDe(HttpContext), Clave(), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 200 <c>CosteoCotizacion</c> sin el conexo en las rutas elegidas; 400 <c>ErrorWeb</c> (forma) o <c>RUTA_INEXISTENTE</c>; 404; 409
    /// <c>COSTEO_NO_LISTO</c>, <c>RUTA_RECHAZADA</c>, <c>SIN_RUTAS_COTIZABLES</c>, <c>PDF_YA_ENVIADO</c>, <c>CONFLICTO_CONCURRENCIA</c> o
    /// <c>IDEMPOTENCY_KEY_REUTILIZADA</c>; 428.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/costeo/servicios-conexos/lineas/quitar")]
    [clsRequiereIfMatch]
    [ProducesResponseType<Aplicacion.Costeo.Services.clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> QuitarConexo(string folio, [FromBody] clsQuitarConexoLineasWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await lineas.QuitarConexoAsync(
            UsuarioId, folio, cuerpo.AEntrada(), clsConcurrenciaHttp.VersionDe(HttpContext), Clave(), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    private string? Clave()
    {
        var clave = Request.Headers[clsCosteoController.EncabezadoIdempotencia].ToString();
        return clave.Length == 0 ? null : clave;
    }
}
