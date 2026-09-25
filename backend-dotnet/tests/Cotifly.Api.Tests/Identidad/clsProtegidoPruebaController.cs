using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Tests.Identidad;

/// <summary>
/// Endpoints protegidos SOLO del host de pruebas (se agregan con <c>AddApplicationPart</c>): ejercitan la tubería real de
/// autenticación (JwtBearer, revocación por jti, políticas por rol) antes de que existan los endpoints de <c>/web/*</c>.
/// </summary>
[ApiController]
[Route("pruebas")]
public sealed class clsProtegidoPruebaController(IClsContextoUsuario usuario) : ControllerBase
{
    [HttpGet("comercial")]
    [Authorize(Policy = clsPoliticas.Comercial)]
    public IActionResult Comercial() => Ok(new
    {
        usuarioId = usuario.UsuarioId,
        username = usuario.NombreUsuario,
        rol = usuario.Rol is { } rol ? clsCodigoEnum.ACodigo(rol) : null,
        autenticado = usuario.EstaAutenticado,
    });

    [HttpGet("gerente")]
    [Authorize(Policy = clsPoliticas.GerenteComercial)]
    public IActionResult Gerente() => Ok(new { rol = "gerente_comercial" });
}
