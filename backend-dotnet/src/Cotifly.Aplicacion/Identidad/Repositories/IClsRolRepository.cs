using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Repositories;

/// <summary>Puerto de los roles internos (<c>Roles</c>) y de su asignación por usuario (<c>UsuariosRoles</c>), script 0002.</summary>
public interface IClsRolRepository
{
    /// <summary>Roles internos activos (con o sin rol externo mapeado).</summary>
    Task<IReadOnlyList<clsRol>> ListarActivosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Roles internos de las asignaciones activas del usuario cuyo rol también está activo, sin repetir (el esquema impide asignar el
    /// mismo rol dos veces), ordenados por el id del rol. Pueden ser varios (script 16).
    /// </summary>
    Task<IReadOnlyList<RolInterno>> ListarRolesAsignadosAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rol interno activo de cada usuario de <paramref name="usuarios"/> (asignación y rol activos). Los usuarios sin asignación no
    /// aparecen (es el <c>rol</c> de la lista de comerciales del tablero). Con varias asignaciones activas, el de
    /// mayor alcance (<c>director</c> &gt; <c>gerente_comercial</c> &gt; <c>comercial</c>).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, RolInterno>> ListarRolesDeUsuariosAsync(IReadOnlyCollection<Guid> usuarios, CancellationToken cancellationToken = default);

    /// <summary>
    /// TODAS las asignaciones del usuario (activas e inactivas), rastreadas, para reconciliarlas en el login
    /// (reactivar una fila inactiva o desactivar una sobrante, sin insertar duplicados; <c>uq_usuariosRoles_usuarioId_rolId</c>). Los
    /// cambios sobre estas entidades se confirman con la unidad de trabajo.
    /// </summary>
    Task<IReadOnlyList<clsUsuarioRol>> ListarAsignacionesAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Agrega una asignación (se confirma con la unidad de trabajo).</summary>
    void AgregarAsignacion(clsUsuarioRol asignacion);
}
