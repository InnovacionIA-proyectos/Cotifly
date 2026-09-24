using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Margen.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsMargenAjusteRepository"/>.</summary>
public sealed class clsMargenAjusteRepository(clsCotiflyDbContext contexto) : IClsMargenAjusteRepository
{
    public void Agregar(clsMargenAjuste ajuste)
    {
        ArgumentNullException.ThrowIfNull(ajuste);
        contexto.MargenAjustes.Add(ajuste);
    }

    public async Task<IReadOnlyList<clsMargenAjuste>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        await contexto.MargenAjustes.AsNoTracking()
            .Where(a => a.CotizacionId == cotizacionId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
}
