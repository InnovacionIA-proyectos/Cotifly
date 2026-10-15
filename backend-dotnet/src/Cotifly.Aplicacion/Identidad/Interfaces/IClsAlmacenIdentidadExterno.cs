using Cotifly.Aplicacion.Identidad.DTOs;

namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// Puerto del almacén corporativo de identidad (base de identidad del portal, la misma de Nexo; QA: <c>dbSolucionesTI_Prueba</c>, #172; solo lectura, ADR-008/ADR-009): personas de
/// <c>tblSAPUsuariosApp</c> y sus roles de ClientePro (<c>tblSAPUsuarioRoles</c> + <c>tblSAPRoles</c>). Reemplaza a la
/// entidad <c>clsUsuarioSapApp</c> que vivía en Dominio (deuda de ADR-008).
/// </summary>
/// <remarks>
/// Cualquier fallo de acceso (base caída, permisos, tiempo agotado) se propaga como excepción: el login lo trata como
/// "almacén no disponible" y responde 401 (fail-closed). Un dato que no se puede interpretar con seguridad (ID con
/// decimales o fuera de rango, nombre vacío, NOMBREUSUARIO repetido) se trata como "no encontrado".
/// </remarks>
public interface IClsAlmacenIdentidadExterno
{
    /// <summary>
    /// Busca a la persona por <paramref name="idUsuario"/> como <c>NOMBREUSUARIO</c> (sin distinguir mayúsculas, según la
    /// collation del almacén) y, si no aparece y es numérico, como <c>ID</c>. <c>null</c> si no existe.
    /// </summary>
    Task<clsUsuarioExterno?> BuscarUsuarioAsync(string idUsuario, CancellationToken cancellationToken = default);

    /// <summary>
    /// Roles de ClientePro de la persona (<c>tblSAPUsuarioRoles.IDUSUARIO</c> = <paramref name="idUsuarioExterno"/>), sin
    /// repetidos. <c>IDUSUARIO</c> es INT y <c>tblSAPUsuariosApp.ID</c> NUMERIC: un ID fuera del rango de INT no puede tener
    /// roles y devuelve una lista vacía.
    /// </summary>
    Task<IReadOnlyList<clsRolExterno>> ObtenerRolesAsync(long idUsuarioExterno, CancellationToken cancellationToken = default);
}
