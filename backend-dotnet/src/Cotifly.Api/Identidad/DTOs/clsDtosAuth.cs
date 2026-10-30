using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Identidad.DTOs;

namespace Cotifly.Api.Identidad.DTOs;

/// <summary><c>LoginClienteProRequest</c>. Los nombres JSON son explícitos (camelCase del patrón SSO de ClientePro).</summary>
public sealed record clsLoginClienteProRequest(
    [property: JsonPropertyName("idUsuario")] string? IdUsuario,
    [property: JsonPropertyName("nombreUsuario")] string? NombreUsuario,
    [property: JsonPropertyName("roles")] IReadOnlyList<string>? Roles);

/// <summary>HU-040.2 (#230): <c>ElegirRolRequest</c>: el ticket del login con varios roles y el código del rol elegido.</summary>
public sealed record clsElegirRolRequest(
    [property: JsonPropertyName("ticket")] string? Ticket,
    [property: JsonPropertyName("rol")] string? Rol);

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

/// <summary>
/// HU-040.2 (#230, ADR-025): <c>EleccionRolRequerida</c>, la respuesta 200 del login cuando la persona tiene varios roles. No trae sesión:
/// el frontend muestra <c>roles</c> y llama a <c>POST /api/auth/elegir-rol</c> con <c>ticketEleccion</c> antes de <c>ticketExpiraAt</c>.
/// </summary>
public sealed record clsEleccionRolResponse(
    [property: JsonPropertyName("requiereEleccionRol")] bool RequiereEleccionRol,
    [property: JsonPropertyName("ticketEleccion")] string TicketEleccion,
    [property: JsonPropertyName("ticketExpiraAt")] DateTimeOffset TicketExpiraAt,
    [property: JsonPropertyName("roles")] IReadOnlyList<clsRolDisponibleRespuesta> Roles,
    [property: JsonPropertyName("user")] clsUsuarioEleccionRespuesta User)
{
    public static clsEleccionRolResponse Desde(clsEleccionRolPendiente eleccion)
    {
        ArgumentNullException.ThrowIfNull(eleccion);
        return new clsEleccionRolResponse(
            RequiereEleccionRol: true,
            eleccion.Ticket,
            eleccion.ExpiraAt,
            [.. eleccion.Roles.Select(r => new clsRolDisponibleRespuesta(r.Codigo, r.Descripcion))],
            new clsUsuarioEleccionRespuesta(eleccion.UsuarioId.ToString("D"), eleccion.Username, eleccion.NombreCompleto));
    }
}

/// <summary>HU-040.2 (#230): <c>RolDisponible</c> (código del claim <c>rol</c> y descripción para el selector).</summary>
public sealed record clsRolDisponibleRespuesta(
    [property: JsonPropertyName("codigo")] string Codigo,
    [property: JsonPropertyName("descripcion")] string Descripcion);

/// <summary>HU-040.2 (#230): <c>UsuarioEleccion</c>, quién está eligiendo (sin rol todavía).</summary>
public sealed record clsUsuarioEleccionRespuesta(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("nombre_completo")] string NombreCompleto);

/// <summary><c>UsuarioSesion</c>: <c>ubicacion</c> es la regional o <c>null</c> (se serializa siempre).</summary>
public sealed record clsUsuarioSesionRespuesta(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("nombre_completo")] string NombreCompleto,
    [property: JsonPropertyName("rol")] string Rol,
    [property: JsonPropertyName("ubicacion"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Ubicacion);
