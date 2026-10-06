using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Costeo.Controllers;

/// <summary>
/// Servicios conexos del catálogo de Nexo (HU-013.3, ADR-018 §4, <c>specs/openapi.yaml</c>): el buscador (sin costos) y agregar
/// conexos a la cotización en Nexo. Solo cotizaciones propias; una ajena responde 404 igual que una inexistente.
/// </summary>
[Route("web")]
public sealed class clsServiciosConexosController(IClsContextoUsuario usuario, IClsServiciosConexosCosteoService conexos)
    : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>PaginaServiciosConexos</c> (solo activos, nunca <c>costoUnitario</c>); 400 filtro fuera de límites; 403
    /// <c>NEXO_SIN_PERMISO</c>; 503 <c>NEXO_NO_DISPONIBLE</c>.
    /// </summary>
    [HttpGet("servicios-conexos")]
    [ProducesResponseType<clsPaginaServiciosConexos>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Buscar(
        [FromQuery] string? q,
        [FromQuery] string? categoria,
        [FromQuery] int? page,
        [FromQuery(Name = "page_size")] int? pageSize,
        CancellationToken cancellationToken) =>
        (await conexos.BuscarAsync(UsuarioId, q, categoria, page, pageSize, cancellationToken)).Coincidir<IActionResult>(p => Ok(p), Error);

    /// <summary>
    /// 200 <c>CosteoCotizacion</c> con los conexos agregados en Nexo e incluidos con su precio (y el <c>ETag</c> nuevo); 400
    /// <c>CONEXO_INVALIDO</c> o <c>ErrorWeb</c> (esquema); 403 <c>NEXO_SIN_PERMISO</c>; 404; 409 <c>COSTEO_NO_LISTO</c>,
    /// <c>LINEA_SIN_COTIZACION_NEXO</c>, <c>PDF_YA_ENVIADO</c>, <c>ESTADO_NO_PERMITE</c> o <c>CONFLICTO_CONCURRENCIA</c>; 428 sin
    /// <c>If-Match</c>; 503 <c>NEXO_NO_DISPONIBLE</c> (con <c>detalle.agregado_en_nexo</c> si solo falló la relectura).
    /// <c>Idempotency-Key</c> opcional.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/costeo/servicios-conexos")]
    [clsRequiereIfMatch]
    [clsIdempotente]
    [ProducesResponseType<clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Agregar(string folio, [FromBody] clsAgregarServiciosConexosWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await conexos.AgregarAsync(UsuarioId, folio, cuerpo.AEntrada(), clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }
}
