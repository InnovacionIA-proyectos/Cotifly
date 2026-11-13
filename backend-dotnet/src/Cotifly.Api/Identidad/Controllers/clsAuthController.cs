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
/// Login SSO desde ClientePro, elección del rol (ADR-025), refresh y logout (<c>/api/auth/*</c> de <c>specs/openapi.yaml</c>, ADR-009). Sin lógica de
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

    /// <summary>
    /// 200 <c>LoginResponse</c> (un solo rol) o 200 <c>EleccionRolRequerida</c> (varios roles: sin sesión, con el ticket de
    /// <c>elegir-rol</c>); 400 cuerpo inválido; 401 <c>{"error":"Credenciales inválidas"}</c>.
    /// </summary>
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
            login => login.Eleccion is { } eleccion ? Ok(clsEleccionRolResponse.Desde(eleccion)) : Ok(clsLoginResponse.Desde(login.Sesion!)),
            error => error.Codigo == clsCodigosAcceso.SolicitudInvalida
                ? BadRequest(new clsErrorWeb(error.Mensaje))
                : Unauthorized(new clsErrorWeb(clsCodigosAcceso.MensajeCredencialesInvalidas)));
    }

    /// <summary>
    /// Elección del rol (ADR-025): 200 <c>LoginResponse</c> con el rol elegido; 400 cuerpo inválido (sin ticket o un <c>rol</c> que no es un
    /// código de rol); 401 <c>{"error":"Inicia sesión de nuevo."}</c> para todo rechazo (ticket vencido, inválido o ya usado, rol no
    /// ofrecido o retirado). El ticket se consume aunque la elección falle. Mismo límite de peticiones por IP que el login.
    /// </summary>
    [HttpPost("elegir-rol")]
    [ProducesResponseType<clsLoginResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized, "application/json")]
    public async Task<IActionResult> ElegirRol(
        [FromBody] clsElegirRolRequest cuerpo,
        [FromServices] IClsEleccionRolService servicio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        ArgumentNullException.ThrowIfNull(servicio);

        var resultado = await servicio.ElegirAsync(new clsSolicitudElegirRol(cuerpo.Ticket, cuerpo.Rol), cancellationToken);

        return resultado.Coincidir<IActionResult>(
            sesion => Ok(clsLoginResponse.Desde(sesion)),
            error => error.Codigo == clsCodigosAcceso.SolicitudInvalida
                ? BadRequest(new clsErrorWeb(error.Mensaje))
                : Unauthorized(new clsErrorWeb(clsCodigosAcceso.MensajeSesionExpirada)));
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
