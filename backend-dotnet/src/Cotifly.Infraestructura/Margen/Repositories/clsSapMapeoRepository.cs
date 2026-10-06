using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Margen.Repositories;

/// <summary>
/// Implementación EF Core de <see cref="IClsSapMapeoRepository"/>. Igual que el Python
/// (<c>MapeoTrqTypeRepo</c>): filtra por <c>salesOrg IN (@salesOrg, '')</c> y ordena <c>salesOrg DESC</c>
/// para que la fila específica gane sobre el comodín.
/// </summary>
public sealed class clsSapMapeoRepository(clsCotiflyDbContext contexto) : IClsSapMapeoRepository
{
    public Task<string?> LineaDeAsync(string trqType, string salesOrg = "", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trqType);
        var tipo = trqType.Trim();
        var organizacion = salesOrg?.Trim() ?? clsSapMapeoTrqType.SalesOrgComodin;

        return contexto.SapMapeoTrqType.AsNoTracking()
            .Where(m => m.TrqType == tipo
                        && (m.SalesOrg == organizacion || m.SalesOrg == clsSapMapeoTrqType.SalesOrgComodin)
                        && m.Activo)
            .OrderByDescending(m => m.SalesOrg)
            .Select(m => m.LineaNegocio)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<string?> TrqTypeDeAsync(string lineaNegocio, string salesOrg = "", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineaNegocio);
        var linea = lineaNegocio.Trim();
        var organizacion = salesOrg?.Trim() ?? clsSapMapeoTrqType.SalesOrgComodin;

        return contexto.SapMapeoTrqType.AsNoTracking()
            .Where(m => m.LineaNegocio == linea
                        && (m.SalesOrg == organizacion || m.SalesOrg == clsSapMapeoTrqType.SalesOrgComodin)
                        && m.Activo)
            .OrderByDescending(m => m.SalesOrg)
            .ThenBy(m => m.TrqType)
            .Select(m => m.TrqType)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
