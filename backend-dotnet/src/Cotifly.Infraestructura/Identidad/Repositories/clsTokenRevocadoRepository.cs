using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Identidad.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsTokenRevocadoRepository"/>.</summary>
public sealed class clsTokenRevocadoRepository(clsCotiflyDbContext contexto) : IClsTokenRevocadoRepository
{
    public void Agregar(clsTokenRevocado token)
    {
        ArgumentNullException.ThrowIfNull(token);
        contexto.TokensRevocados.Add(token);
    }

    public async Task<IReadOnlyList<clsJtiRevocado>> ListarVigentesAsync(DateTimeOffset ahora, CancellationToken cancellationToken = default) =>
        await contexto.TokensRevocados.AsNoTracking()
            .Where(t => t.ExpiraAt > ahora)
            .Select(t => new clsJtiRevocado(t.Jti, t.ExpiraAt))
            .ToListAsync(cancellationToken);

    public Task<int> PurgarVencidosAsync(DateTimeOffset ahora, CancellationToken cancellationToken = default) =>
        contexto.TokensRevocados.Where(t => t.ExpiraAt <= ahora).ExecuteDeleteAsync(cancellationToken);
}
