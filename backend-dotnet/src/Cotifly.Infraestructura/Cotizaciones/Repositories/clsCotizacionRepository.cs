using System.Globalization;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Cotizaciones.Repositories;

/// <summary>
/// Implementación EF Core de <see cref="IClsCotizacionRepository"/> sobre el agregado de HU-021 (ADR-019): la cabecera
/// (<c>CotizacionesSolicitud</c>) con sus rutas, camiones, mercancías y conexos, leídos con consultas divididas (una por tabla).
/// </summary>
public sealed class clsCotizacionRepository(clsCotiflyDbContext contexto, ILogger<clsCotizacionRepository>? logger = null) : IClsCotizacionRepository
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public void Agregar(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        contexto.CotizacionesSolicitud.Add(cotizacion);
    }

    public void AgregarHistorialPdf(clsCotizacionPdfHistorial historial)
    {
        ArgumentNullException.ThrowIfNull(historial);
        contexto.CotizacionPdfsHistorial.Add(historial);
    }

    public async Task<IReadOnlyList<clsCotizacionPdfHistorial>> ListarHistorialPdfAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        await contexto.CotizacionPdfsHistorial
            .Where(h => h.CotizacionId == cotizacionId)
            .OrderBy(h => h.CreatedAt)
            .ThenBy(h => h.Id)
            .ToListAsync(cancellationToken);

    public void AgregarArchivoLicitacion(clsArchivoLicitacion archivo)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        contexto.ArchivosLicitacion.Add(archivo);
    }

    public Task<clsArchivoLicitacionResumen?> ObtenerResumenArchivoLicitacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        contexto.ArchivosLicitacion.AsNoTracking()
            .Where(a => a.CotizacionId == cotizacionId)
            .Select(a => new clsArchivoLicitacionResumen(a.NombreArchivo, a.Hoja, a.TamanoBytes))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<clsArchivoLicitacion?> ObtenerArchivoLicitacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        contexto.ArchivosLicitacion.AsNoTracking().FirstOrDefaultAsync(a => a.CotizacionId == cotizacionId, cancellationToken);

    public async Task<bool> ReclamarReenvioCosteoAsync(
        Guid cotizacionId, DateTimeOffset updatedAtLeido, DateTimeOffset ahora, CancellationToken cancellationToken = default)
    {
        var filas = await contexto.CotizacionesSolicitud
            .Where(c => c.Id == cotizacionId && c.UpdatedAt == updatedAtLeido
                        && (c.EstadoCosteo == EstadoCosteo.ErrorEnvio || c.EstadoCosteo == EstadoCosteo.PendienteEnvio))
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.EstadoCosteo, (EstadoCosteo?)EstadoCosteo.PendienteEnvio).SetProperty(c => c.UpdatedAt, ahora),
                cancellationToken);
        return filas == 1;
    }

    public async Task<IReadOnlyList<clsConsultaCosteoPendiente>> ListarConsultasCosteoPendientesAsync(
        DateTimeOffset ahora, int lote, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lote, 1);
        return await contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(c => c.CosteoSiguienteConsultaAt != null && c.CosteoSiguienteConsultaAt <= ahora)
            .OrderBy(c => c.CosteoSiguienteConsultaAt)
            .Take(lote)
            .Select(c => new clsConsultaCosteoPendiente(c.Id, c.CosteoSiguienteConsultaAt!.Value))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ReclamarConsultaCosteoAsync(
        Guid cotizacionId, DateTimeOffset esperada, DateTimeOffset reclamoHasta, CancellationToken cancellationToken = default)
    {
        var filas = await contexto.CotizacionesSolicitud
            .Where(c => c.Id == cotizacionId && c.CosteoSiguienteConsultaAt == esperada)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CosteoSiguienteConsultaAt, (DateTimeOffset?)reclamoHasta), cancellationToken);
        return filas == 1;
    }

    public Task<int> ReprogramarEntregadasSinPrecioAsync(DateTimeOffset ahora, CancellationToken cancellationToken = default) =>
        contexto.CotizacionesSolicitud
            .Where(c => c.EstadoCosteo == EstadoCosteo.Entregada && c.SolicitudNexoId != null && c.PrecioFinal == null && c.PdfPath == null
                        && c.MotivoEstadoCosteo == null && c.CosteoSiguienteConsultaAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CosteoSiguienteConsultaAt, (DateTimeOffset?)ahora), cancellationToken);

    public Task<clsCotizacionSolicitud?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        ConAgregado().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<clsCotizacionSolicitud?> ObtenerPorFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return ConAgregado().FirstOrDefaultAsync(c => c.Folio == buscado, cancellationToken);
    }

    public Task<clsCotizacionSolicitud?> ObtenerPorFolioDelUsuarioAsync(string folio, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return ConAgregado().FirstOrDefaultAsync(c => c.Folio == buscado && c.UsuarioId == usuarioId, cancellationToken);
    }

    public Task<clsCotizacionSolicitud?> ObtenerPorIdDelUsuarioAsync(Guid id, Guid usuarioId, CancellationToken cancellationToken = default) =>
        ConAgregado().FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == usuarioId, cancellationToken);

    public Task<bool> ExisteFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return contexto.CotizacionesSolicitud.AnyAsync(c => c.Folio == buscado, cancellationToken);
    }

    /// <summary>
    /// Relee la cabecera y cada fila de su agregado que está en memoria, en pocas consultas (una por tabla), y las deja como
    /// <c>Unchanged</c>: también después de un <c>ChangeTracker.Clear()</c> (conflicto de versión), así un guardado posterior no
    /// confunde las rutas ya existentes con filas nuevas. Una fila que ya no existe en la base queda fuera del seguimiento.
    /// </summary>
    public async Task RecargarAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.Rutas.ToList();
        var rutaIds = rutas.Select(r => r.Id).ToList();
        await RecargarFilasAsync(contexto.CotizacionLineasSolicitud, rutas, r => rutaIds.Contains(r.Id), r => r.Id, cancellationToken);
        await RecargarFilasAsync(
            contexto.CotizacionVehiculosLineaSolicitud, [.. rutas.SelectMany(r => r.Vehiculos)], v => rutaIds.Contains(v.LineaSolicitudId), v => v.Id, cancellationToken);
        await RecargarFilasAsync(
            contexto.CotizacionMercancias, [.. rutas.SelectMany(r => r.Mercancias)], m => rutaIds.Contains(m.LineaSolicitudId), m => m.Id, cancellationToken);
        await RecargarFilasAsync(
            contexto.CotizacionServiciosConexos, [.. rutas.SelectMany(r => r.Conexos)], c => rutaIds.Contains(c.LineaSolicitudId), c => c.Id, cancellationToken);

        // La cabecera al final: así, si EF la vuelve a seguir, sus rutas ya están seguidas como existentes.
        await contexto.Entry(cotizacion).ReloadAsync(cancellationToken);
    }

    private async Task RecargarFilasAsync<T>(
        DbSet<T> tabla, IReadOnlyList<T> enMemoria, System.Linq.Expressions.Expression<Func<T, bool>> filtro, Func<T, Guid> id,
        CancellationToken cancellationToken)
        where T : class
    {
        if (enMemoria.Count == 0)
        {
            return;
        }

        var deBase = (await tabla.AsNoTracking().Where(filtro).ToListAsync(cancellationToken)).ToDictionary(id);
        foreach (var fila in enMemoria)
        {
            var entrada = contexto.Entry(fila);
            if (!deBase.TryGetValue(id(fila), out var actual))
            {
                entrada.State = EntityState.Detached;
                continue;
            }

            entrada.CurrentValues.SetValues(actual);
            entrada.OriginalValues.SetValues(actual);
            entrada.State = EntityState.Unchanged;
        }
    }

    public async Task<IReadOnlyList<clsCotizacionSolicitud>> ListarPorUsuarioAsync(
        Guid usuarioId, int limite = IClsCotizacionRepository.LimitePorDefecto, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limite, 1);

        return await contexto.CotizacionesSolicitud.AsNoTracking()
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
        var cabeceras = await CabecerasToleranteAsync(usuarioId, limite, cancellationToken);
        if (cabeceras.Count == 0)
        {
            return [];
        }

        // Línea de negocio y margen de la ruta 1 de cada una, y cuántas líneas de negocio distintas tienen sus rutas.
        var ids = cabeceras.Select(c => c.Id).ToList();
        var rutas = await contexto.CotizacionLineasSolicitud.AsNoTracking()
            .Where(r => ids.Contains(r.CotizacionSolicitudId))
            .Select(r => new { r.CotizacionSolicitudId, r.Orden, r.LineaNegocioId, r.MargenAplicado, r.OrigenMargen })
            .ToListAsync(cancellationToken);
        var lineasNegocio = await contexto.MargenesPolitica.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.LineaNegocio, cancellationToken);
        var porCotizacion = rutas.GroupBy(r => r.CotizacionSolicitudId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Orden).ToList());

        return [.. cabeceras.Select(c =>
        {
            if (!porCotizacion.TryGetValue(c.Id, out var suyas) || suyas.Count == 0)
            {
                return new clsCotizacionConLinea(c, null);
            }

            var primera = suyas[0];
            var linea = suyas.Select(r => r.LineaNegocioId).Distinct().Count() > 1
                ? clsCotizacionConLinea.Varias
                : primera.LineaNegocioId is { } lineaId ? lineasNegocio.GetValueOrDefault(lineaId) : null;
            return new clsCotizacionConLinea(c, linea, primera.MargenAplicado, primera.OrigenMargen);
        })];
    }

    /// <summary>
    /// Cabeceras del comercial (HU-014.1, mediano plazo): si una fila trae un código fuera de catálogo (el convertidor del enum la
    /// rechaza), en vez de tumbar toda la lista se leen una por una y se omiten las inválidas (queda en el log).
    /// </summary>
    private async Task<List<clsCotizacionSolicitud>> CabecerasToleranteAsync(Guid usuarioId, int limite, CancellationToken cancellationToken)
    {
        var consulta = contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(c => c.UsuarioId == usuarioId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Folio)
            .Take(limite);
        try
        {
            return await consulta.ToListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var ids = await consulta.Select(c => c.Id).ToListAsync(cancellationToken);
            var leidas = new List<clsCotizacionSolicitud>(ids.Count);
            foreach (var id in ids)
            {
                try
                {
                    if (await contexto.CotizacionesSolicitud.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) is { } fila)
                    {
                        leidas.Add(fila);
                    }
                }
                catch (Exception porFila) when (porFila is InvalidOperationException or ArgumentException)
                {
                    _logger.LogWarning(porFila, "La cotización {Id} tiene un valor fuera de catálogo: se omite de \"Mis cotizaciones\".", id);
                }
            }

            return leidas;
        }
    }

    public async Task<int> ObtenerUltimoConsecutivoDelDiaAsync(string iniciales, string segmentoFecha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentoFecha);
        if (!clsInicialesComercial.EsValida(iniciales))
        {
            throw new ArgumentException("Las iniciales del folio deben ser dos letras A-Z en mayúscula.", nameof(iniciales));
        }

        if (segmentoFecha.Length != 8 || !segmentoFecha.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("La fecha del folio debe ser DDMMAAAA.", nameof(segmentoFecha));
        }

        // PCN-[nit-]INICIALES-DDMMAAAA-NNN (ADR-011): los tres últimos caracteres son el consecutivo. Sin filtrar por comercial
        // ni por versión: cuenta cualquier fila con esa forma (las -vN no terminan en NNN), así el siguiente nunca está tomado.
        // Dos patrones: sin NIT, o con un NIT (termina en dígito) antes de las iniciales; ninguno admite comodines del usuario.
        var sufijo = $"{iniciales}-{segmentoFecha}-[0-9][0-9][0-9]";
        var sinNit = $"PCN-{sufijo}";
        var conNit = $"PCN-%[0-9]-{sufijo}";
        var folios = await contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Folio, sinNit) || EF.Functions.Like(c.Folio, conNit))
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
        var maxima = await contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(c => c.Folio == buscado || EF.Functions.Like(c.Folio, patron, @"\"))
            .MaxAsync(c => (int?)c.Version, cancellationToken);
        return maxima ?? 0;
    }

    /// <summary>La cabecera con todo su agregado (rutas, camiones, mercancías y conexos), en consultas divididas.</summary>
    private IQueryable<clsCotizacionSolicitud> ConAgregado() =>
        contexto.CotizacionesSolicitud
            .Include(c => c.Rutas).ThenInclude(r => r.Vehiculos)
            .Include(c => c.Rutas).ThenInclude(r => r.Mercancias)
            .Include(c => c.Rutas).ThenInclude(r => r.Conexos)
            .AsSplitQuery();

    /// <summary>Escapa los comodines de LIKE (<c>%</c>, <c>_</c>, <c>[</c>) con <c>\</c>: un folio heredado puede traer <c>_</c>.</summary>
    private static string EscaparLike(string valor) =>
        valor.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal);
}
