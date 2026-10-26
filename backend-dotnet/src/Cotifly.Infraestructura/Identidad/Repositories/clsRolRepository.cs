using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Identidad.Repositories;

/// <summary>
/// Implementación EF Core de <see cref="IClsRolRepository"/> (<c>Roles</c> y <c>UsuariosRoles</c>). HU-037 (#210): solo lee los roles cuyo
/// código conoce este backend (<see cref="RolInterno"/>). Un rol nuevo en el catálogo (por ejemplo, <c>director</c> con el script 15 y un
/// backend anterior) se ignora: nadie entra con él, en vez de fallar el login de todos al convertir el código. Al leer el catálogo, cada
/// rol activo ignorado deja un warning con su id (sin leer el código, que no se puede convertir), para que no pase en silencio.
/// </summary>
public sealed class clsRolRepository(clsCotiflyDbContext contexto, ILogger<clsRolRepository> logger) : IClsRolRepository
{
    /// <summary>Los roles internos que conoce este backend.</summary>
    private static readonly List<RolInterno> _conocidos = [.. Enum.GetValues<RolInterno>()];

    public async Task<IReadOnlyList<clsRol>> ListarActivosAsync(CancellationToken cancellationToken = default)
    {
        var ignorados = await contexto.Roles.AsNoTracking()
            .Where(r => r.Activo && !_conocidos.Contains(r.Codigo))
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        if (ignorados.Count > 0)
        {
            logger.LogWarning(
                "El catálogo de roles tiene {Cantidad} rol(es) activo(s) con un código que este backend no conoce (id {Ids}): se ignoran y nadie "
                + "entra con ellos. Desplegar la imagen que los conoce o desactivarlos.",
                ignorados.Count, string.Join(", ", ignorados));
        }

        return await contexto.Roles.AsNoTracking().Where(r => r.Activo && _conocidos.Contains(r.Codigo)).OrderBy(r => r.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RolInterno>> ListarRolesAsignadosAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        await (from asignacion in contexto.UsuariosRoles.AsNoTracking()
               join rol in contexto.Roles.AsNoTracking() on asignacion.RolId equals rol.Id
               where asignacion.UsuarioId == usuarioId && asignacion.Activo && rol.Activo && _conocidos.Contains(rol.Codigo)
               orderby rol.Id
               select rol.Codigo)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, RolInterno>> ListarRolesDeUsuariosAsync(
        IReadOnlyCollection<Guid> usuarios, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        if (usuarios.Count == 0)
        {
            return new Dictionary<Guid, RolInterno>();
        }

        var ids = usuarios.Distinct().ToList();
        var filas = await (from asignacion in contexto.UsuariosRoles.AsNoTracking()
                           join rol in contexto.Roles.AsNoTracking() on asignacion.RolId equals rol.Id
                           // HU-037.3 (#211): con el director sin regional son todos los usuarios; un solo parámetro (OPENJSON), sin tope.
                           where EF.Parameter(ids).Contains(asignacion.UsuarioId) && asignacion.Activo && rol.Activo && _conocidos.Contains(rol.Codigo)
                           orderby rol.Id
                           select new { asignacion.UsuarioId, rol.Codigo })
            .ToListAsync(cancellationToken);
        return filas.GroupBy(f => f.UsuarioId).ToDictionary(g => g.Key, g => g.First().Codigo);
    }

    public void AgregarAsignacion(clsUsuarioRol asignacion)
    {
        ArgumentNullException.ThrowIfNull(asignacion);
        contexto.UsuariosRoles.Add(asignacion);
    }
}
