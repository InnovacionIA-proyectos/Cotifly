using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Margen.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsMargenPoliticaRepository"/>.</summary>
public sealed class clsMargenPoliticaRepository(clsCotiflyDbContext contexto) : IClsMargenPoliticaRepository
{
    public Task<clsMargenPolitica?> ObtenerPorLineaAsync(string lineaNegocio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineaNegocio);
        var buscada = lineaNegocio.Trim();
        return contexto.MargenesPolitica.FirstOrDefaultAsync(m => m.LineaNegocio == buscada, cancellationToken);
    }

    public Task<clsMargenPolitica?> ObtenerPorIdAsync(int id, CancellationToken cancellationToken = default) =>
        contexto.MargenesPolitica.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<IReadOnlyList<clsMargenPolitica>> ListarAsync(CancellationToken cancellationToken = default) =>
        await contexto.MargenesPolitica.AsNoTracking().OrderBy(m => m.LineaNegocio).ToListAsync(cancellationToken);
}
