namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Rol interno de Cotifly (HU-010, ADR-009): <c>comercial</c> | <c>gerente_comercial</c>.
/// No se persiste en <c>UsuariosWeb</c>; sale de <c>RolesMapeo</c> al iniciar sesión.
/// </summary>
public enum RolInterno
{
    Comercial,
    GerenteComercial,
}
