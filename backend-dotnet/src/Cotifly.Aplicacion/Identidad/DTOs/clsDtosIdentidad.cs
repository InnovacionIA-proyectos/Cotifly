namespace Cotifly.Aplicacion.Identidad.DTOs;

/// <summary>
/// Cuerpo de <c>POST /api/auth/login-clientepro</c> (<c>LoginClienteProRequest</c>). <see cref="NombreUsuario"/> y
/// <see cref="Roles"/> son informativos: solo se auditan, nunca identifican ni autorizan (ADR-009).
/// </summary>
public sealed record clsSolicitudLoginClientePro(string? IdUsuario, string? NombreUsuario = null, IReadOnlyList<string>? Roles = null);

/// <summary>Usuario de la sesión (<c>UsuarioSesion</c> del contrato). <see cref="Ubicacion"/> es la regional o <c>null</c>.</summary>
public sealed record clsUsuarioSesion(Guid Id, string Username, string Email, string NombreCompleto, string Rol, string? Ubicacion);

/// <summary>Par de tokens emitido por el login o el refresh (<c>LoginResponse</c> del contrato).</summary>
public sealed record clsSesionEmitida(string Access, string Refresh, clsUsuarioSesion Usuario, DateTimeOffset AccesoExpiraAt);

/// <summary>
/// Datos del logout: el refresh del body (opcional) y, si el access del header era válido, su <c>jti</c>, su vencimiento y
/// su usuario.
/// </summary>
public sealed record clsSolicitudLogout(string? Refresh, string? Jti = null, DateTimeOffset? AccesoExpiraAt = null, Guid? UsuarioId = null);

/// <summary>
/// Persona del almacén corporativo (<c>dbsolucionesti.tblSAPUsuariosApp</c>, solo lectura). Nunca trae <c>CLAVE</c>.
/// <see cref="IdUsuarioExterno"/> es el <c>ID</c> (NUMERIC sin decimales) convertido a <see cref="long"/>.
/// </summary>
public sealed record clsUsuarioExterno(long IdUsuarioExterno, string NombreUsuario, string? NombreCompleto, string? Email);

/// <summary>Rol de ClientePro de una persona (<c>tblSAPUsuarioRoles.IDROLES</c> + <c>tblSAPRoles.DESCRIPCION</c> informativa).</summary>
public sealed record clsRolExterno(int IdRol, string? Descripcion);

/// <summary>Access token firmado con su <c>jti</c> y su vencimiento.</summary>
public sealed record clsTokenAccesoEmitido(string Token, string Jti, DateTimeOffset ExpiraAt);
