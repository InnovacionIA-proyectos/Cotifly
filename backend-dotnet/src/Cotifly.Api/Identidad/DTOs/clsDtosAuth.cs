using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Identidad.DTOs;

namespace Cotifly.Api.Identidad.DTOs;

/// <summary><c>LoginClienteProRequest</c>. Los nombres JSON son explícitos (camelCase del patrón SSO de ClientePro).</summary>
public sealed record clsLoginClienteProRequest(
    [property: JsonPropertyName("idUsuario")] string? IdUsuario,
    [property: JsonPropertyName("nombreUsuario")] string? NombreUsuario,
    [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles);

/// <summary><c>RefreshRequest</c> y <c>LogoutRequest</c>.</summary>
public sealed record clsRefreshRequest([property: JsonPropertyName("refresh")] string? Refresh);

/// <summary>
/// <c>LoginResponse</c>: <c>/api/auth/*</c> usa camelCase (<c>debeCambiarPassword</c>) salvo <c>user.nombre_completo</c>,
/// tal como lo define <c>specs/openapi.yaml</c> (<c>UsuarioSesion</c>).
/// </summary>
public sealed record clsLoginResponse(
    [property: JsonPropertyName("access")] string Access,
    [property: JsonPropertyName("refresh")] string Refresh,
    [property: JsonPropertyName("user")] clsUsuarioSesionRespuesta User,
    [property: JsonPropertyName("debeCambiarPassword")] bool DebeCambiarPassword)
{
    /// <summary>Siempre <c>debeCambiarPassword = false</c> en la puerta SSO (Cotifly no maneja contraseñas).</summary>
    public static clsLoginResponse Desde(clsSesionEmitida sesion)
    {
        ArgumentNullException.ThrowIfNull(sesion);
        var u = sesion.Usuario;
        return new clsLoginResponse(
            sesion.Access,
            sesion.Refresh,
            new clsUsuarioSesionRespuesta(u.Id.ToString("D"), u.Username, u.Email, u.NombreCompleto, u.Rol, u.Ubicacion),
            DebeCambiarPassword: false);
    }
}

/// <summary><c>UsuarioSesion</c>: <c>ubicacion</c> es la regional o <c>null</c> (se serializa siempre).</summary>
public sealed record clsUsuarioSesionRespuesta(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("nombre_completo")] string NombreCompleto,
    [property: JsonPropertyName("rol")] string Rol,
    [property: JsonPropertyName("ubicacion"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Ubicacion);
