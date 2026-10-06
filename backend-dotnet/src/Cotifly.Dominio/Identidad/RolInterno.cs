namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Rol interno de Cotifly (HU-010, ADR-009): <c>comercial</c> | <c>gerente_comercial</c>.
/// Catálogo en <c>dbo_cotifly.Roles</c> (con su rol externo de ClientePro) y asignación por usuario en
/// <c>dbo_cotifly.UsuariosRoles</c> (script 0002). Viaja en el claim <c>rol</c> del JWT.
/// </summary>
public enum RolInterno
{
    Comercial,
    GerenteComercial,
}
