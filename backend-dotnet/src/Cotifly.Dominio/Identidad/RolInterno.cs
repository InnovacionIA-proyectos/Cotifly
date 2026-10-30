namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Rol interno de Cotifly (HU-010, ADR-009): <c>comercial</c> | <c>gerente_comercial</c> | <c>director</c> (HU-037, ADR-016 §11).
/// Catálogo en <c>dbo_cotifly.Roles</c> (con su rol externo de ClientePro) y asignación por usuario en
/// <c>dbo_cotifly.UsuariosRoles</c> (script 0002; <c>director</c> llega con el script 15). Viaja en el claim <c>rol</c> del JWT.
/// Rol único: con varios roles externos mapeados gana <c>director</c> &gt; <c>gerente_comercial</c> &gt; <c>comercial</c>. Multi-rol
/// (HU-040, ADR-025): con varias asignaciones activas la persona elige el rol al entrar.
/// </summary>
public enum RolInterno
{
    Comercial,
    GerenteComercial,

    /// <summary>HU-037 (#210): usa todo lo del gerente comercial y ve todas las regionales (#211).</summary>
    Director,
}

/// <summary>Reglas de permiso por rol interno, para revalidar en los casos de uso (defensa en profundidad) sin repetir la lista.</summary>
public static class clsRolInternoExtensiones
{
    /// <summary>
    /// <c>true</c> para <c>gerente_comercial</c> y <c>director</c> (HU-037, #210): el tablero y el listado de la regional, el detalle y
    /// el PDF de cotizaciones ajenas (según su alcance) y la administración de líneas de negocio y márgenes.
    /// </summary>
    public static bool EsGerencial(this RolInterno rol) => rol is RolInterno.GerenteComercial or RolInterno.Director;

    /// <inheritdoc cref="EsGerencial(RolInterno)"/>
    public static bool EsGerencial(this RolInterno? rol) => rol is { } valor && valor.EsGerencial();
}
