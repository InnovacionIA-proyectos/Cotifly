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

    public Task<clsUsuarioWeb?> ObtenerPorIdUsuarioExternoAsync(long idUsuarioExterno, CancellationToken cancellationToken = default) =>
        contexto.UsuariosWeb.FirstOrDefaultAsync(u => u.IdUsuarioExterno == idUsuarioExterno, cancellationToken);

    public async Task<clsUsuarioWeb?> ObtenerPorEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var buscado = email.Trim();

        // La collation de la columna no distingue mayúsculas. Un correo repetido no identifica a nadie (ADR-011).
        var encontrados = await contexto.UsuariosWeb.Where(u => u.Email == buscado).Take(2).ToListAsync(cancellationToken);
        return encontrados.Count == 1 ? encontrados[0] : null;
    }
}
