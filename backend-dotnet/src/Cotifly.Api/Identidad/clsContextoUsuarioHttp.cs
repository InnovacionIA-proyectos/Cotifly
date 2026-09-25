using System.Security.Claims;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Api.Identidad;

/// <summary><see cref="IClsContextoUsuario"/> a partir de los claims del access token de la petición HTTP en curso.</summary>
public sealed class clsContextoUsuarioHttp(IHttpContextAccessor accesor) : IClsContextoUsuario
{
    private ClaimsPrincipal? Principal =>
        accesor.HttpContext?.User is { Identity.IsAuthenticated: true } usuario ? usuario : null;

    public bool EstaAutenticado => UsuarioId is not null;

    public Guid? UsuarioId => Guid.TryParse(Principal?.FindFirst(clsClaimsCotifly.Sub)?.Value, out var id) ? id : null;

    public string? NombreUsuario => Principal?.FindFirst(clsClaimsCotifly.Username)?.Value;

    public RolInterno? Rol =>
        clsCodigoEnum.TryDesdeCodigo<RolInterno>(Principal?.FindFirst(clsClaimsCotifly.Rol)?.Value, out var rol) ? rol : null;
}
