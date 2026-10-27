using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Administracion.Controllers;

/// <summary>
/// HU-016.1 (#154, decisión P4b del 2026-10-27): <c>GET /web/lineas-negocio</c>, las líneas de negocio activas para el desplegable de la
/// captura. Política <c>Comercial</c> de la base (comercial, gerente y director). No trae el margen: la política de margen es de
/// <see cref="clsLineasNegocioController"/> (<c>/web/admin/lineas-negocio</c>, solo gerencia).
/// </summary>
[Route("web/lineas-negocio")]
public sealed class clsLineasNegocioCatalogoController(IClsContextoUsuario usuario, IClsLineasNegocioCatalogoService lineas) : clsControladorWebBase(usuario)
{
    /// <summary>200 lista de <c>LineaNegocioOpcion</c> (activas, por nombre).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<clsLineaNegocioOpcion>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken) => Ok(await lineas.ListarActivasAsync(cancellationToken));
}
