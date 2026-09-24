using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Cotizaciones.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsCotizacionRepository"/>.</summary>
public sealed class clsCotizacionRepository(clsCotiflyDbContext contexto) : IClsCotizacionRepository
{
    public void Agregar(clsCotizacion cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        contexto.Cotizaciones.Add(cotizacion);
    }

    public Task<clsCotizacion?> ObtenerPorFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return contexto.Cotizaciones.FirstOrDefaultAsync(c => c.Folio == buscado, cancellationToken);
    }

    public Task<clsCotizacion?> ObtenerPorFolioDelUsuarioAsync(string folio, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return contexto.Cotizaciones.FirstOrDefaultAsync(c => c.Folio == buscado && c.UsuarioId == usuarioId, cancellationToken);
    }

    public Task<clsCotizacion?> ObtenerPorIdDelUsuarioAsync(Guid id, Guid usuarioId, CancellationToken cancellationToken = default) =>
        contexto.Cotizaciones.FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == usuarioId, cancellationToken);

    public Task<bool> ExisteFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return contexto.Cotizaciones.AnyAsync(c => c.Folio == buscado, cancellationToken);
    }

    public async Task<IReadOnlyList<clsCotizacion>> ListarPorUsuarioAsync(
        Guid usuarioId, int limite = IClsCotizacionRepository.LimitePorDefecto, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limite, 1);

        return await contexto.Cotizaciones.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Folio)
            .Take(limite)
            .ToListAsync(cancellationToken);
    }

    public async Task<clsCotizacion?> MarcarSeguimientoAsync(
        string folio,
        Guid usuarioId,
        bool? ganada,
        string? motivoRechazo,
        DateTimeOffset cuando,
        CancellationToken cancellationToken = default)
    {
        var cotizacion = await ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        cotizacion?.MarcarSeguimiento(ganada, motivoRechazo, cuando);
        return cotizacion;
    }
}
