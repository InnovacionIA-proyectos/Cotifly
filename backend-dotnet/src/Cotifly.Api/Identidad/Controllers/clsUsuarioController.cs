using Cotifly.Api.Comun;
using Cotifly.Api.Identidad.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Identidad.Controllers;

/// <summary>
/// Datos propios del comercial autenticado (<c>GET/PUT /web/usuario/telefono</c>): el teléfono que imprime el PDF ("Cel").
/// </summary>
[Route("web/usuario")]
public sealed class clsUsuarioController(IClsContextoUsuario usuario, IClsTelefonoComercialService telefono) : clsControladorWebBase(usuario)
{
    /// <summary>200 <c>TelefonoComercial</c> (<c>null</c> si nunca se capturó); 401 si el comercial del token ya no existe.</summary>
    [HttpGet("telefono")]
    [ProducesResponseType<clsTelefonoComercial>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ObtenerTelefono(CancellationToken cancellationToken)
    {
        var resultado = await telefono.ObtenerAsync(UsuarioId, cancellationToken);
        return resultado.Coincidir<IActionResult>(t => Ok(t), Error);
    }

    /// <summary>200 <c>TelefonoComercial</c>; 400 fuera del patrón <c>^\+?[0-9 ]{7,20}$</c>; 401 si el comercial ya no existe.</summary>
    [HttpPut("telefono")]
    [ProducesResponseType<clsTelefonoComercial>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GuardarTelefono([FromBody] clsTelefonoRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await telefono.GuardarAsync(UsuarioId, cuerpo.Telefono, cancellationToken);
        return resultado.Coincidir<IActionResult>(t => Ok(t), Error);
    }
}
