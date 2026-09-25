using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Metricas.Controllers;

/// <summary>
/// Tablero de actividad (<c>x-estado: borrador</c> en <c>specs/openapi.yaml</c>; HU-010.1 define los KPI definitivos). El
/// comercial ve solo su fila; el gerente, los comerciales de su regional (solo la suya si su regional es nula).
/// </summary>
[Route("web/metricas")]
public sealed class clsMetricasController(IClsContextoUsuario usuario, IClsMetricasService metricas) : clsControladorWebBase(usuario)
{
    /// <summary>200 lista de <c>ActividadComercial</c>.</summary>
    [HttpGet("actividad")]
    [ProducesResponseType<IReadOnlyList<clsActividadComercial>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Actividad(CancellationToken cancellationToken)
    {
        var resultado = await metricas.ObtenerActividadAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(f => Ok(f), Error);
    }

    /// <summary>200 lista de <c>AjustesMargenComercial</c>.</summary>
    [HttpGet("ajustes-margen")]
    [ProducesResponseType<IReadOnlyList<clsAjustesMargenComercial>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AjustesMargen(CancellationToken cancellationToken)
    {
        var resultado = await metricas.ObtenerAjustesMargenAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(f => Ok(f), Error);
    }
}
