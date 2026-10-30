using Cotifly.Dominio.Identidad;

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

/// <summary>HU-040.2 (#230): un rol que el usuario puede elegir (<c>codigo</c> del claim <c>rol</c> y su descripción del catálogo).</summary>
public sealed record clsRolDisponible(string Codigo, string Descripcion);

/// <summary>
/// HU-040.2 (#230, ADR-025): el login verificó a una persona con varios roles y NO emitió sesión. <see cref="Ticket"/> es lo único que
/// acepta <c>POST /api/auth/elegir-rol</c> (de un solo uso, vence en <see cref="ExpiraAt"/>).
/// </summary>
public sealed record clsEleccionRolPendiente(
    string Ticket, DateTimeOffset ExpiraAt, IReadOnlyList<clsRolDisponible> Roles, Guid UsuarioId, string Username, string NombreCompleto);

/// <summary>
/// HU-040.2 (#230): resultado del login. Con un solo rol, <see cref="Sesion"/> (igual que antes de HU-040); con varios,
/// <see cref="Eleccion"/>. Siempre uno de los dos.
/// </summary>
public sealed record clsResultadoLogin
{
    private clsResultadoLogin(clsSesionEmitida? sesion, clsEleccionRolPendiente? eleccion)
    {
        Sesion = sesion;
        Eleccion = eleccion;
    }

    public clsSesionEmitida? Sesion { get; }

    public clsEleccionRolPendiente? Eleccion { get; }

    public static clsResultadoLogin ConSesion(clsSesionEmitida sesion) => new(sesion ?? throw new ArgumentNullException(nameof(sesion)), null);

    public static clsResultadoLogin ConEleccion(clsEleccionRolPendiente eleccion) => new(null, eleccion ?? throw new ArgumentNullException(nameof(eleccion)));
}

/// <summary>HU-040.2 (#230): cuerpo de <c>POST /api/auth/elegir-rol</c> (<c>ElegirRolRequest</c>).</summary>
public sealed record clsSolicitudElegirRol(string? Ticket, string? Rol);

/// <summary>HU-040.2 (#230): ticket de elección emitido (JWT firmado de vida corta).</summary>
public sealed record clsTicketEleccionEmitido(string Ticket, string Jti, DateTimeOffset ExpiraAt);

/// <summary>
/// HU-040.2 (#230): ticket de elección con firma, emisor, audiencia, tipo y vigencia válidos: el usuario, el <c>jti</c> (para
/// consumirlo una sola vez), su vencimiento y los roles que se le ofrecieron.
/// </summary>
public sealed record clsTicketEleccionValido(Guid UsuarioId, string NombreUsuario, string Jti, DateTimeOffset ExpiraAt, IReadOnlyList<RolInterno> Roles);

/// <summary>
/// Datos del logout: el refresh del body (opcional) y, si el access del header era válido, su <c>jti</c>, su vencimiento y
/// su usuario.
/// </summary>
public sealed record clsSolicitudLogout(string? Refresh, string? Jti = null, DateTimeOffset? AccesoExpiraAt = null, Guid? UsuarioId = null);

/// <summary>
/// Persona del almacén corporativo (<c>tblSAPUsuariosApp</c> de la base de identidad del portal, la misma de Nexo; QA: <c>dbSolucionesTI_Prueba</c>, #172; solo lectura). Nunca trae <c>CLAVE</c>.
/// <see cref="IdUsuarioExterno"/> es el <c>ID</c> (NUMERIC sin decimales) convertido a <see cref="long"/>.
/// </summary>
public sealed record clsUsuarioExterno(long IdUsuarioExterno, string NombreUsuario, string? NombreCompleto, string? Email);

/// <summary>Rol de ClientePro de una persona (<c>tblSAPUsuarioRoles.IDROLES</c> + <c>tblSAPRoles.DESCRIPCION</c> informativa).</summary>
public sealed record clsRolExterno(int IdRol, string? Descripcion);

/// <summary>Access token firmado con su <c>jti</c> y su vencimiento.</summary>
public sealed record clsTokenAccesoEmitido(string Token, string Jti, DateTimeOffset ExpiraAt);
