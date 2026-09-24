using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Dominio.Auditoria;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Auditoria.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsAuditoriaRepository"/>.</summary>
public sealed class clsAuditoriaRepository(clsCotiflyDbContext contexto) : IClsAuditoriaRepository
{
    public void Registrar(clsEventoHerramienta evento)
    {
        ArgumentNullException.ThrowIfNull(evento);
        contexto.EventosHerramienta.Add(evento);
    }

    public async Task<IReadOnlyList<clsEventoHerramienta>> ListarPorThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var hilo = threadId.Trim();
        return await contexto.EventosHerramienta.AsNoTracking()
            .Where(e => e.ThreadId == hilo)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
