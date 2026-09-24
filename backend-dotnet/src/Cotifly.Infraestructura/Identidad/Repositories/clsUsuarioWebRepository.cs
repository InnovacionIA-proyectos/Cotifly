using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Identidad.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsUsuarioWebRepository"/>.</summary>
public sealed class clsUsuarioWebRepository(clsCotiflyDbContext contexto) : IClsUsuarioWebRepository
{
    public void Agregar(clsUsuarioWeb usuario)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        contexto.UsuariosWeb.Add(usuario);
    }

    public Task<clsUsuarioWeb?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        contexto.UsuariosWeb.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<clsUsuarioWeb?> ObtenerPorNombreUsuarioAsync(string nombreUsuario, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombreUsuario);
        var buscado = nombreUsuario.Trim();
        return contexto.UsuariosWeb.FirstOrDefaultAsync(u => u.NombreUsuario == buscado, cancellationToken);
    }
}
