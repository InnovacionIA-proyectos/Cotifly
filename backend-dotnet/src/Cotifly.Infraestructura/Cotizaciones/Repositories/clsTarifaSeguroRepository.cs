using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Cotizaciones.Repositories;

/// <summary>Implementación EF de <see cref="IClsTarifaSeguroRepository"/> (HU-045.2, #264).</summary>
public sealed class clsTarifaSeguroRepository(clsCotiflyDbContext contexto) : IClsTarifaSeguroRepository
{
    public async Task<decimal?> ObtenerTasaVigenteAsync(CancellationToken cancellationToken = default) =>
        await contexto.TarifasSeguro.AsNoTracking()
            .Where(t => t.Activa)
            .OrderByDescending(t => t.VigenteDesde)
            .Select(t => (decimal?)t.Tasa)
            .FirstOrDefaultAsync(cancellationToken);
}
