using System.Globalization;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
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

    public Task RecargarAsync(clsCotizacion cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return contexto.Entry(cotizacion).ReloadAsync(cancellationToken);
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
    public async Task<IReadOnlyList<clsCotizacionConLinea>> ListarConLineaPorUsuarioAsync(
        Guid usuarioId, int limite = IClsCotizacionRepository.LimitePorDefecto, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limite, 1);

        var filas = await (
                from c in contexto.Cotizaciones.AsNoTracking()
                where c.UsuarioId == usuarioId
                join l in contexto.MargenesPolitica.AsNoTracking() on c.LineaNegocioId equals (int?)l.Id into lineas
                from l in lineas.DefaultIfEmpty()
                orderby c.CreatedAt descending, c.Folio descending
                select new { Cotizacion = c, Linea = l == null ? null : l.LineaNegocio })
            .Take(limite)
            .ToListAsync(cancellationToken);

        return filas.Select(f => new clsCotizacionConLinea(f.Cotizacion, f.Linea)).ToList();
    }

    public async Task<int> ObtenerUltimoConsecutivoDelDiaAsync(Guid usuarioId, string segmentoFecha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentoFecha);
        if (segmentoFecha.Length != 8 || !segmentoFecha.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("La fecha del folio debe ser DDMMAAAA.", nameof(segmentoFecha));
        }

        // PCN-[nit-]DDMMAAAA-NNN (ADR-011): los tres últimos caracteres son el consecutivo. Solo versión 1 (las -vN no cuentan).
        var patron = $"PCN-%{segmentoFecha}-[0-9][0-9][0-9]";
        var folios = await contexto.Cotizaciones.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId && c.Version == 1 && EF.Functions.Like(c.Folio, patron))
            .Select(c => c.Folio)
            .ToListAsync(cancellationToken);

        return folios
            .Select(f => int.TryParse(f.AsSpan(f.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    public async Task<int> ObtenerVersionMaximaAsync(string folioBase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folioBase);
        var buscado = folioBase.Trim();
        var patron = EscaparLike(buscado) + "-v%";
        var maxima = await contexto.Cotizaciones.AsNoTracking()
            .Where(c => c.Folio == buscado || EF.Functions.Like(c.Folio, patron, @"\"))
            .MaxAsync(c => (int?)c.Version, cancellationToken);
        return maxima ?? 0;
    }

    /// <summary>Escapa los comodines de LIKE (<c>%</c>, <c>_</c>, <c>[</c>) con <c>\</c>: un folio heredado puede traer <c>_</c>.</summary>
    private static string EscaparLike(string valor) =>
        valor.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal);
}
