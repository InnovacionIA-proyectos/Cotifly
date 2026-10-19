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

    public async Task<int> DesvincularRutasAsync(IReadOnlyCollection<Guid> rutas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rutas);
        if (rutas.Count == 0)
        {
            return 0;
        }

        var ids = rutas.Select(r => (Guid?)r).ToList();
        var vinculados = await contexto.MargenAjustes.Where(a => ids.Contains(a.LineaSolicitudId)).ToListAsync(cancellationToken);
        return vinculados.Count(a => a.DesvincularRuta());
    }
}
