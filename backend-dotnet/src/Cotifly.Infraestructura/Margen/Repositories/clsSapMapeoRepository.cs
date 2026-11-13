using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Margen.Repositories;

/// <summary>
/// Implementación EF Core de <see cref="IClsSapMapeoRepository"/>: filtra por <c>salesOrg IN (@salesOrg, '')</c> y ordena
/// <c>salesOrg DESC</c> para que la fila específica gane sobre el comodín. La línea se cruza por <c>lineaNegocioId</c>
/// (FK a <c>MargenesPolitica</c>), no por la columna de texto <c>lineaNegocio</c>.
/// </summary>
public sealed class clsSapMapeoRepository(clsCotiflyDbContext contexto) : IClsSapMapeoRepository
{
    public Task<string?> LineaDeAsync(string trqType, string salesOrg = "", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trqType);
        var tipo = trqType.Trim();
        var organizacion = salesOrg?.Trim() ?? clsSapMapeoTrqType.SalesOrgComodin;

        return (from m in contexto.SapMapeoTrqType.AsNoTracking()
                join p in contexto.MargenesPolitica.AsNoTracking() on m.LineaNegocioId equals p.Id
                where m.TrqType == tipo
                      && (m.SalesOrg == organizacion || m.SalesOrg == clsSapMapeoTrqType.SalesOrgComodin)
                      && m.Activo
                orderby m.SalesOrg descending
                select p.LineaNegocio)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<string?> TrqTypeDeAsync(int lineaNegocioId, string salesOrg = "", CancellationToken cancellationToken = default)
    {
        var organizacion = salesOrg?.Trim() ?? clsSapMapeoTrqType.SalesOrgComodin;

        return contexto.SapMapeoTrqType.AsNoTracking()
            .Where(m => m.LineaNegocioId == lineaNegocioId
                        && (m.SalesOrg == organizacion || m.SalesOrg == clsSapMapeoTrqType.SalesOrgComodin)
                        && m.Activo)
            .OrderByDescending(m => m.SalesOrg)
            .ThenBy(m => m.TrqType)
            .Select(m => m.TrqType)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
