using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Seguros;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Cotizaciones.Repositories;

/// <summary>
/// Implementación EF de <see cref="IClsPoliticaSegurosRepository"/> (HU-046.3, #269): tres lecturas <c>AsNoTracking</c> por llamada (los
/// tramos activos, sus reglas con medidas activas y los conexos de esas medidas), sin caché: un cambio de Datos en las tablas se ve en la
/// siguiente petición. Las medidas de cada tramo salen por <c>nombre</c> y luego por <c>codigo</c>, y los conexos de cada medida ordenados
/// (contrato del 2026-11-10; orden estable, las tablas no tienen columna de orden).
/// </summary>
public sealed class clsPoliticaSegurosRepository(clsCotiflyDbContext contexto) : IClsPoliticaSegurosRepository
{
    public async Task<clsPoliticaSeguros> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        var tramos = await contexto.PoliticaSeguroTramos.AsNoTracking()
            .Where(t => t.Activo)
            .OrderBy(t => t.ValorDesde)
            .Select(t => new { t.Id, t.Nombre, t.ValorDesde, t.ValorHasta })
            .ToListAsync(cancellationToken);
        if (tramos.Count == 0)
        {
            return clsPoliticaSeguros.Vacia;
        }

        var reglas = await (
                from regla in contexto.PoliticaSeguroTramoMedidas.AsNoTracking()
                join medida in contexto.PoliticaSeguroMedidas.AsNoTracking() on regla.MedidaId equals medida.Id
                where medida.Activa
                select new { regla.TramoId, MedidaId = medida.Id, medida.Codigo, medida.Nombre, medida.EsCobrable, regla.Nivel })
            .ToListAsync(cancellationToken);
        var conexos = (await (
                    from conexo in contexto.PoliticaSeguroMedidaConexos.AsNoTracking()
                    join medida in contexto.PoliticaSeguroMedidas.AsNoTracking() on conexo.MedidaId equals medida.Id
                    where medida.Activa
                    select new { conexo.MedidaId, conexo.CodigoServicio })
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.MedidaId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.CodigoServicio.Trim()).Order(StringComparer.Ordinal).ToList());

        return new clsPoliticaSeguros(tramos.Select(t => new clsTramoPoliticaSeguro(
            t.Id, t.Nombre, t.ValorDesde, t.ValorHasta,
            [
                .. reglas.Where(r => r.TramoId == t.Id)
                    .OrderBy(r => r.Nombre, StringComparer.Ordinal).ThenBy(r => r.Codigo, StringComparer.Ordinal)
                    .Select(r => new clsMedidaTramoSeguro(r.Codigo, r.Nombre, r.EsCobrable, r.Nivel, conexos.GetValueOrDefault(r.MedidaId) ?? [])),
            ])));
    }
}
