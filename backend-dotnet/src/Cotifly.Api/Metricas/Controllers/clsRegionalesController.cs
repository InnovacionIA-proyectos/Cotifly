using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Metricas.Controllers;

/// <summary>
/// <c>GET /web/regionales</c>, las regionales del selector del tablero (<c>/web/metricas/regional</c>) y del listado
/// regional (<c>/web/cotizaciones/regional</c>). <c>gerente_comercial</c> (la suya) o <c>director</c> (todas); un comercial recibe 403.
/// </summary>
[Route("web")]
public sealed class clsRegionalesController(
    IClsContextoUsuario usuario,
    IClsRegionalesService regionales) : clsControladorWebBase(usuario)
{
    /// <summary>200 <c>Regionales</c>; 403 si no es gerente ni director; 409 <c>REGIONAL_NO_ASIGNADA</c>; 503 <c>REGIONAL_NO_DISPONIBLE</c>.</summary>
    [HttpGet("regionales")]
    [Authorize(Policy = clsPoliticas.GerenteComercial)]
    [ProducesResponseType<clsRegionales>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken) =>
        (await regionales.ListarAsync(cancellationToken)).Coincidir<IActionResult>(r => Ok(r), Error);
}
