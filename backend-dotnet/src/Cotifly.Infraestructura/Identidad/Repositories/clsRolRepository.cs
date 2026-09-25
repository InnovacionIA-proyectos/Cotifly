using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Identidad.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsRolRepository"/> (<c>Roles</c> y <c>UsuariosRoles</c>).</summary>
public sealed class clsRolRepository(clsCotiflyDbContext contexto) : IClsRolRepository
{
    public async Task<IReadOnlyList<clsRol>> ListarActivosAsync(CancellationToken cancellationToken = default) =>
        await contexto.Roles.AsNoTracking().Where(r => r.Activo).OrderBy(r => r.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RolInterno>> ListarRolesAsignadosAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        await (from asignacion in contexto.UsuariosRoles.AsNoTracking()
               join rol in contexto.Roles.AsNoTracking() on asignacion.RolId equals rol.Id
               where asignacion.UsuarioId == usuarioId && asignacion.Activo && rol.Activo
               orderby rol.Id
               select rol.Codigo)
            .ToListAsync(cancellationToken);

    public void AgregarAsignacion(clsUsuarioRol asignacion)
    {
        ArgumentNullException.ThrowIfNull(asignacion);
        contexto.UsuariosRoles.Add(asignacion);
    }
}
