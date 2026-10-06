using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Repositories;

/// <summary>Puerto de los roles internos (<c>Roles</c>) y de su asignación por usuario (<c>UsuariosRoles</c>), script 0002.</summary>
public interface IClsRolRepository
{
    /// <summary>Roles internos activos (con o sin rol externo mapeado).</summary>
    Task<IReadOnlyList<clsRol>> ListarActivosAsync(CancellationToken cancellationToken = default);

    /// <summary>Roles internos de las asignaciones activas del usuario cuyo rol también está activo (0 o 1 por el esquema).</summary>
    Task<IReadOnlyList<RolInterno>> ListarRolesAsignadosAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rol interno activo de cada usuario de <paramref name="usuarios"/> (asignación y rol activos; el esquema admite uno). Los
    /// usuarios sin asignación no aparecen (HU-010.1: <c>rol</c> de la lista de comerciales del tablero).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, RolInterno>> ListarRolesDeUsuariosAsync(IReadOnlyCollection<Guid> usuarios, CancellationToken cancellationToken = default);

    /// <summary>Agrega una asignación (se confirma con la unidad de trabajo).</summary>
    void AgregarAsignacion(clsUsuarioRol asignacion);
}
