using System.Globalization;
using System.Text.Json;
using Cotifly.Api.Comun;
using Cotifly.Api.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cotifly.Api.Identidad.Controllers;

/// <summary>
/// Login SSO desde ClientePro, refresh y logout (<c>/api/auth/*</c> de <c>specs/openapi.yaml</c>, ADR-009). Sin lógica de
/// negocio: delega en los casos de uso de Aplicación. Límite de peticiones por IP (<see cref="clsConfiguracionAutenticacion.PoliticaLimiteAuth"/>).
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(clsConfiguracionAutenticacion.PoliticaLimiteAuth)]
public sealed class clsAuthController : ControllerBase
{
    /// <summary>Largo máximo del cuerpo de refresh/logout que se intenta leer.</summary>
    private const long LargoMaximoCuerpo = 4096;

    /// <summary>200 <c>LoginResponse</c>; 400 cuerpo inválido; 401 <c>{"error":"Credenciales inválidas"}</c>.</summary>
    [HttpPost("login-clientepro")]
    [ProducesResponseType<clsLoginResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized, "application/json")]
    public async Task<IActionResult> LoginClientePro(
        [FromBody] clsLoginClienteProRequest cuerpo,
        [FromServices] IClsLoginClienteProService servicio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        ArgumentNullException.ThrowIfNull(servicio);

        var resultado = await servicio.IniciarSesionAsync(
            new clsSolicitudLoginClientePro(cuerpo.IdUsuario, cuerpo.NombreUsuario, cuerpo.Roles), cancellationToken);

        return resultado.Coincidir<IActionResult>(
            sesion => Ok(clsLoginResponse.Desde(sesion)),
            error => error.Codigo == clsCodigosAcceso.SolicitudInvalida
                ? BadRequest(new clsErrorWeb(error.Mensaje))
                : Unauthorized(new clsErrorWeb(clsCodigosAcceso.MensajeCredencialesInvalidas)));
    }

    /// <summary>200 <c>LoginResponse</c> con el par rotado; cualquier otro caso (incluido un cuerpo inválido) 401.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<clsLoginResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized, "application/json")]
    public async Task<IActionResult> Refrescar([FromServices] IClsRefreshTokenService servicio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servicio);

        var cuerpo = await LeerCuerpoAsync(cancellationToken);
        var resultado = await servicio.RenovarAsync(cuerpo?.Refresh, cancellationToken);

        return resultado.Coincidir<IActionResult>(
            sesion => Ok(clsLoginResponse.Desde(sesion)),
            _ => Unauthorized(new clsErrorWeb(clsCodigosAcceso.MensajeSesionExpirada)));
    }

    /// <summary>Siempre 204 (anti-enumeración): revoca el refresh del cuerpo y, si el access del header es válido, su <c>jti</c>.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CerrarSesion([FromServices] IClsLogoutService servicio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servicio);

        var cuerpo = await LeerCuerpoAsync(cancellationToken);
        string? jti = null;
        DateTimeOffset? expira = null;
        Guid? usuarioId = null;
        if (User.Identity?.IsAuthenticated == true)
        {
            jti = User.FindFirst(clsClaimsCotifly.Jti)?.Value;
            expira = long.TryParse(User.FindFirst(clsClaimsCotifly.Expira)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : null;
            usuarioId = Guid.TryParse(User.FindFirst(clsClaimsCotifly.Sub)?.Value, out var id) ? id : null;
        }

        await servicio.CerrarSesionAsync(new clsSolicitudLogout(cuerpo?.Refresh, jti, expira, usuarioId), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Lee <c>{"refresh": "..."}</c> sin la validación automática de MVC: en refresh y logout un cuerpo ausente o mal
    /// formado no es un 400 del contrato (refresh responde 401 y logout siempre 204).
    /// </summary>
    private async Task<clsRefreshRequest?> LeerCuerpoAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength is 0 or > LargoMaximoCuerpo || !Request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await Request.ReadFromJsonAsync<clsRefreshRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
