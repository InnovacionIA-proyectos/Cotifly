using Cotifly.Api.Administracion.DTOs;
using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Administracion.Controllers;

/// <summary>
/// Políticas de margen por línea de negocio (<c>specs/openapi.yaml</c> tag <c>admin</c>). Solo
/// <c>gerente_comercial</c>: política <see cref="clsPoliticas.GerenteComercial"/> aquí y revalidación del rol en el caso de uso.
/// No crea ni borra líneas.
/// </summary>
[Route("web/admin/lineas-negocio")]
[Authorize(Policy = clsPoliticas.GerenteComercial)]
public sealed class clsLineasNegocioController(IClsContextoUsuario usuario, IClsLineasNegocioAdminService lineas) : clsControladorWebBase(usuario)
{
    /// <summary>200 lista de <c>LineaNegocio</c> (activas e inactivas); 403 si no es gerente.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<clsLineaNegocioAdmin>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        var resultado = await lineas.ListarAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(l => Ok(l), Error);
    }

    /// <summary>200 <c>LineaNegocio</c> actualizada (queda auditada); 400 cuerpo inválido; 403; 404 si la línea no existe.</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<clsLineaNegocioAdmin>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Actualizar(int id, [FromBody] clsActualizarLineaNegocioRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await lineas.ActualizarAsync(id, new clsCambioLineaNegocio(cuerpo.Margen, cuerpo.MetodoCalculo, cuerpo.IsActive), cancellationToken);
        return resultado.Coincidir<IActionResult>(l => Ok(l), Error);
    }
}
