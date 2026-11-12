using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Metricas.Controllers;

/// <summary>
/// Tablero de métricas (ADR-016; tag <c>metricas</c> de <c>specs/openapi.yaml</c>): <c>comercial</c> (propias, comercial o
/// gerente) y <c>regional</c> (<c>gerente_comercial</c> o <c>director</c>).
/// </summary>
[Route("web/metricas")]
public sealed class clsMetricasController(
    IClsContextoUsuario usuario,
    IClsTableroMetricasService tablero) : clsControladorWebBase(usuario)
{
    /// <summary>200 <c>MetricasComercial</c>; 400 fechas inválidas.</summary>
    [HttpGet("comercial")]
    [ProducesResponseType<clsMetricasComercial>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Comercial([FromQuery] string? desde, [FromQuery] string? hasta, CancellationToken cancellationToken)
    {
        var resultado = await tablero.ObtenerComercialAsync(new clsConsultaMetricas(desde, hasta), cancellationToken);
        return resultado.Coincidir<IActionResult>(m => Ok(m), Error);
    }

    /// <summary>
    /// 200 <c>MetricasRegional</c>. <c>anio</c> (año completo) y <c>anio</c>+<c>mes</c> (mes completo), en fechas de
    /// Bogotá, reemplazan a <c>desde</c>/<c>hasta</c>. 400 fechas, <c>anio</c>, <c>mes</c> o <c>usuario_id</c> inválidos (o <c>mes</c> sin
    /// <c>anio</c>); 403 si no es gerente ni director, o <c>REGIONAL_NO_PERMITIDA</c> si el gerente pide otra regional;
    /// 404 comercial fuera del alcance; 409 <c>REGIONAL_NO_ASIGNADA</c>; 503 <c>REGIONAL_NO_DISPONIBLE</c>. <c>regional</c>: la del
    /// director (sin ella, todas).
    /// </summary>
    [HttpGet("regional")]
    [Authorize(Policy = clsPoliticas.GerenteComercial)]
    [ProducesResponseType<clsMetricasRegional>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Regional(
        [FromQuery] string? desde,
        [FromQuery] string? hasta,
        [FromQuery(Name = "usuario_id")] string? usuarioId,
        [FromQuery] string? anio,
        [FromQuery] string? mes,
        [FromQuery] string? regional,
        CancellationToken cancellationToken)
    {
        var resultado = await tablero.ObtenerRegionalAsync(new clsConsultaMetricas(desde, hasta, usuarioId, anio, mes, regional), cancellationToken);
        return resultado.Coincidir<IActionResult>(m => Ok(m), Error);
    }
}
