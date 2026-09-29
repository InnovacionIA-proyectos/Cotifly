using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Metricas.Controllers;

/// <summary>
/// Tablero de métricas (HU-010.1, ADR-016; tag <c>metricas</c> de <c>specs/openapi.yaml</c>): <c>comercial</c> (propias, comercial o
/// gerente) y <c>regional</c> (solo <c>gerente_comercial</c>). <c>actividad</c> y <c>ajustes-margen</c> son el borrador anterior,
/// obsoletos en el contrato: el comercial ve solo su fila; el gerente, los comerciales de su regional (solo la suya si su
/// regional es nula).
/// </summary>
[Route("web/metricas")]
public sealed class clsMetricasController(
    IClsContextoUsuario usuario,
    IClsMetricasService metricas,
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
    /// 200 <c>MetricasRegional</c>; 400 fechas o <c>usuario_id</c> inválidos; 403 si no es gerente; 404 comercial fuera de la
    /// regional; 409 <c>REGIONAL_NO_ASIGNADA</c>; 503 <c>REGIONAL_NO_DISPONIBLE</c>.
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
        CancellationToken cancellationToken)
    {
        var resultado = await tablero.ObtenerRegionalAsync(new clsConsultaMetricas(desde, hasta, usuarioId), cancellationToken);
        return resultado.Coincidir<IActionResult>(m => Ok(m), Error);
    }

    /// <summary>200 lista de <c>ActividadComercial</c> (obsoleto).</summary>
    [HttpGet("actividad")]
    [ProducesResponseType<IReadOnlyList<clsActividadComercial>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Actividad(CancellationToken cancellationToken)
    {
        var resultado = await metricas.ObtenerActividadAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(f => Ok(f), Error);
    }

    /// <summary>200 lista de <c>AjustesMargenComercial</c> (obsoleto).</summary>
    [HttpGet("ajustes-margen")]
    [ProducesResponseType<IReadOnlyList<clsAjustesMargenComercial>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AjustesMargen(CancellationToken cancellationToken)
    {
        var resultado = await metricas.ObtenerAjustesMargenAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(f => Ok(f), Error);
    }
}
