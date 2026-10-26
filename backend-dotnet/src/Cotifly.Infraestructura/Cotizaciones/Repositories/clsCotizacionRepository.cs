using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Auditoria;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.Data.SqlClient;
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

    public void RegistrarConexosPendientesNexo(clsConexosPendientesNexo pendientes)
    {
        ArgumentNullException.ThrowIfNull(pendientes);
        var entrada = JsonSerializer.Serialize(new { folio = pendientes.Folio, linea_solicitud_id = pendientes.LineaSolicitudId });
        var salida = JsonSerializer.Serialize(new { conexos_nexo_previos = pendientes.Previos });
        var evento = clsEventoHerramienta.Registrar(clsConexosPendientesNexo.Herramienta, entrada, salida, true, 0);
        contexto.EventosHerramienta.Add(evento.Valor);
    }

    public void RegistrarEnvioNexo(clsEnvioNexoRegistrado envio)
    {
        ArgumentNullException.ThrowIfNull(envio);
        var entrada = JsonSerializer.Serialize(new { folio = envio.Folio, solicitud_nexo_id = envio.SolicitudNexoId.Trim() });
        // HU-031.1 (#194): equivalencias_vehiculo solo si el alias tradujo algún código (las filas anteriores no la traen: vacío).
        var salida = envio.EquivalenciasVehiculo.Count == 0
            ? JsonSerializer.Serialize(new { codigos_cubicador = envio.CodigosCubicadorEnviados })
            : JsonSerializer.Serialize(new { codigos_cubicador = envio.CodigosCubicadorEnviados, equivalencias_vehiculo = envio.EquivalenciasVehiculo });
        var evento = clsEventoHerramienta.Registrar(clsEnvioNexoRegistrado.Herramienta, entrada, salida, true, 0);
        contexto.EventosHerramienta.Add(evento.Valor);
    }

    public async Task<string?> ObtenerCotizacionNexoIdAsync(string solicitudNexoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudNexoId);
        var id = solicitudNexoId.Trim();
        return await contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(c => c.SolicitudNexoId == id && c.CotizacionNexoId != null)
            .Select(c => c.CotizacionNexoId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> ObtenerEquivalenciasVehiculoAsync(string solicitudNexoId, CancellationToken cancellationToken = default)
    {
        var equivalencias = new Dictionary<string, string>(StringComparer.Ordinal);
        if (await SalidaEnvioNexoAsync(solicitudNexoId, cancellationToken) is not { } salida)
        {
            return equivalencias;
        }

        try
        {
            if (JsonNode.Parse(salida)?["equivalencias_vehiculo"] is JsonObject objeto)
            {
                foreach (var (codigo, enviado) in objeto)
                {
                    if (!string.IsNullOrWhiteSpace(codigo) && enviado is JsonValue valor && valor.TryGetValue<string>(out var texto) && !string.IsNullOrWhiteSpace(texto))
                    {
                        equivalencias[codigo] = texto;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return equivalencias;
    }

    /// <summary>El <c>outputResumen</c> del último registro del envío de <paramref name="solicitudNexoId"/>, o <c>null</c>.</summary>
    private async Task<string?> SalidaEnvioNexoAsync(string solicitudNexoId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudNexoId);
        var marca = $"\"solicitud_nexo_id\":{JsonSerializer.Serialize(solicitudNexoId.Trim())}";
        return await contexto.EventosHerramienta.AsNoTracking()
            .Where(e => e.Herramienta == clsEnvioNexoRegistrado.Herramienta && e.Input != null && e.Input.Contains(marca))
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.OutputResumen)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool?> ObtenerCodigosCubicadorEnviadosAsync(string solicitudNexoId, CancellationToken cancellationToken = default)
    {
        var salida = await SalidaEnvioNexoAsync(solicitudNexoId, cancellationToken);
        if (salida is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(salida)?["codigos_cubicador"]?.GetValue<bool>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public async Task<clsConexosPendientesNexo?> ObtenerConexosPendientesNexoAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var marca = $"\"folio\":{JsonSerializer.Serialize(folio.Trim())}";
        var evento = await contexto.EventosHerramienta.AsNoTracking()
            .Where(e => e.Herramienta == clsConexosPendientesNexo.Herramienta && e.Input != null && e.Input.Contains(marca))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (evento?.Input is not { } input || evento.OutputResumen is not { } output)
        {
            return null;
        }

        try
        {
            var ruta = JsonNode.Parse(input)?["linea_solicitud_id"]?.GetValue<Guid>();
            var previos = JsonNode.Parse(output)?["conexos_nexo_previos"]?.AsArray().Select(n => n!.GetValue<string>()).ToList();
            return ruta is { } id && previos is not null ? new clsConexosPendientesNexo(folio.Trim(), id, previos) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public void RegistrarEnvioConexosNexo(clsEnvioConexosNexo envio)
    {
        ArgumentNullException.ThrowIfNull(envio);
        var entrada = JsonSerializer.Serialize(new
        {
            folio = envio.Folio,
            solicitud_nexo_id = envio.SolicitudNexoId,
            linea_solicitud_id = envio.LineaSolicitudId,
        });
        var salida = JsonSerializer.Serialize(new
        {
            intento = envio.Intento,
            estado = envio.Estado,
            conexos = envio.Conexos,
            conexos_catalogo = envio.ConexosCatalogo,
            conexos_nexo_previos = envio.PreviosNexo,
            espera_desde = envio.EsperaDesde,
        });
        var evento = clsEventoHerramienta.Registrar(clsEnvioConexosNexo.Herramienta, entrada, salida,
            envio.Estado is not (clsEnvioConexosNexo.Fallido or clsEnvioConexosNexo.EnEspera), 0);
        contexto.EventosHerramienta.Add(evento.Valor);
    }

    public async Task<IReadOnlyList<clsEnvioConexosNexo>> ListarEnviosConexosNexoAsync(
        string folio, string solicitudNexoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudNexoId);
        var marcaFolio = $"\"folio\":{JsonSerializer.Serialize(folio.Trim())}";
        var marcaSolicitud = $"\"solicitud_nexo_id\":{JsonSerializer.Serialize(solicitudNexoId.Trim())}";
        var eventos = await contexto.EventosHerramienta.AsNoTracking()
            .Where(e => e.Herramienta == clsEnvioConexosNexo.Herramienta && e.Input != null && e.Input.Contains(marcaFolio) && e.Input.Contains(marcaSolicitud))
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);
        var envios = new List<clsEnvioConexosNexo>(eventos.Count);
        foreach (var evento in eventos)
        {
            try
            {
                var ruta = JsonNode.Parse(evento.Input!)?["linea_solicitud_id"]?.GetValue<Guid>();
                var salida = evento.OutputResumen is { } texto ? JsonNode.Parse(texto) : null;
                var intento = salida?["intento"]?.GetValue<int>();
                var estado = salida?["estado"]?.GetValue<string>();
                var conexos = salida?["conexos"]?.AsArray().Select(n => n!.GetValue<string>()).ToList();
                var previos = salida?["conexos_nexo_previos"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
                var catalogo = salida?["conexos_catalogo"] is JsonArray ids ? ids.Select(n => n!.GetValue<string>()).ToList() : null;
                var esperaDesde = salida?["espera_desde"] is JsonValue desde ? desde.GetValue<DateTimeOffset>() : (DateTimeOffset?)null;
                if (ruta is { } id && intento is { } numero && estado is not null && conexos is not null)
                {
                    envios.Add(new clsEnvioConexosNexo(folio.Trim(), solicitudNexoId.Trim(), id, numero, estado, conexos, previos)
                    {
                        ConexosCatalogo = catalogo,
                        EsperaDesde = esperaDesde,
                    });
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // Un registro ilegible no se usa (queda en la tabla para revisarlo).
            }
        }

        return envios;
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
                // HU-023.3 (#158): el reclamo sube versionNegocio, así ninguna mutación con un If-Match anterior (recalcular camiones,
                // margen, conexos) se guarda mientras el reenvío está en curso.
                s => s.SetProperty(c => c.EstadoCosteo, (EstadoCosteo?)EstadoCosteo.PendienteEnvio).SetProperty(c => c.UpdatedAt, ahora)
                    .SetProperty(c => c.VersionNegocio, c => c.VersionNegocio + 1),
                cancellationToken);
        return filas == 1;
    }

    public async Task<IReadOnlyList<clsRutaPorCubicar>> ListarRutasPorCubicarAsync(
        DateTimeOffset ahora, int lote, Guid? cotizacionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lote, 1);
        return await contexto.Set<clsLineaSolicitud>().AsNoTracking()
            .Where(l => l.EstadoCubicacion == EstadoCubicacion.Pendiente && (l.CubicacionDisponibleAt == null || l.CubicacionDisponibleAt <= ahora))
            .Where(l => cotizacionId == null || l.CotizacionSolicitudId == cotizacionId)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.CotizacionSolicitudId).ThenBy(l => l.Orden)
            .Take(lote)
            .Select(l => new clsRutaPorCubicar(l.Id, l.CotizacionSolicitudId, l.CubicacionDisponibleAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ReclamarRutaPorCubicarAsync(
        Guid rutaId, DateTimeOffset? disponibleLeido, DateTimeOffset reclamoHasta, CancellationToken cancellationToken = default)
    {
        var filas = await contexto.Set<clsLineaSolicitud>()
            .Where(l => l.Id == rutaId && l.EstadoCubicacion == EstadoCubicacion.Pendiente && l.CubicacionDisponibleAt == disponibleLeido)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.CubicacionDisponibleAt, (DateTimeOffset?)reclamoHasta), cancellationToken);
        return filas == 1;
    }

    public async Task<IReadOnlyList<Guid>> ListarCubicadasSinEnviarAsync(
        DateTimeOffset sinCambiosDesde, int lote, Guid? cotizacionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lote, 1);
        var rutas = contexto.Set<clsLineaSolicitud>();
        return await contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(c => cotizacionId == null || c.Id == cotizacionId)
            .Where(c => c.EstadoCosteo == EstadoCosteo.PendienteEnvio && c.SolicitudNexoId == null && c.UpdatedAt <= sinCambiosDesde
                        && rutas.Any(l => l.CotizacionSolicitudId == c.Id && l.EstadoCubicacion != null)
                        && !rutas.Any(l => l.CotizacionSolicitudId == c.Id
                                           && (l.EstadoCubicacion == EstadoCubicacion.Pendiente || l.EstadoCubicacion == EstadoCubicacion.Fallida)))
            .OrderBy(c => c.UpdatedAt)
            .Take(lote)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
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
        LeerAgregadoAsync(() => ConAgregado().FirstOrDefaultAsync(c => c.Id == id, cancellationToken), cancellationToken);

    public Task<clsCotizacionSolicitud?> ObtenerPorFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return LeerAgregadoAsync(() => ConAgregado().FirstOrDefaultAsync(c => c.Folio == buscado, cancellationToken), cancellationToken);
    }

    public Task<clsCotizacionSolicitud?> ObtenerPorFolioDelUsuarioAsync(string folio, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return LeerAgregadoAsync(
            () => ConAgregado().FirstOrDefaultAsync(c => c.Folio == buscado && c.UsuarioId == usuarioId, cancellationToken), cancellationToken);
    }

    public Task<clsCotizacionSolicitud?> ObtenerPorIdDelUsuarioAsync(Guid id, Guid usuarioId, CancellationToken cancellationToken = default) =>
        LeerAgregadoAsync(() => ConAgregado().FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == usuarioId, cancellationToken), cancellationToken);

    /// <summary>
    /// HU-021: el agregado se lee con una consulta por tabla (cabecera, rutas, camiones, mercancías, conexos) mientras otra petición
    /// puede estar guardando el mismo agregado en una transacción; SQL Server puede elegir la lectura como víctima de un interbloqueo
    /// (error 1205). Fuera de una transacción la lectura no tiene efectos y se repite hasta tres veces; dentro de una, el error sube
    /// (SQL Server ya revirtió la transacción).
    /// </summary>
    private async Task<T> LeerAgregadoAsync<T>(Func<Task<T>> consulta, CancellationToken cancellationToken)
    {
        const int Intentos = 3;
        for (var intento = 1; ; intento++)
        {
            try
            {
                return await consulta();
            }
            catch (Exception ex) when (intento < Intentos && contexto.Database.CurrentTransaction is null && EsInterbloqueo(ex))
            {
                _logger.LogWarning("La lectura de la cotización fue víctima de un interbloqueo (intento {Intento}); se repite.", intento);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * intento), cancellationToken);
            }
        }
    }

    private static bool EsInterbloqueo(Exception ex) =>
        ex is SqlException { Number: 1205 } || (ex.InnerException is { } interna && EsInterbloqueo(interna));

    public Task<bool> ExisteFolioAsync(string folio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var buscado = folio.Trim();
        return contexto.CotizacionesSolicitud.AnyAsync(c => c.Folio == buscado, cancellationToken);
    }

    /// <summary>
    /// Vuelve a leer el agregado completo de la base y deja <paramref name="cotizacion"/> (la MISMA instancia, que siguen usando los
    /// llamadores) igual a la base: limpia el rastreador de cambios, lee la cabecera con sus rutas, camiones, mercancías y conexos con
    /// <see cref="ConAgregado"/>, reemplaza las rutas en memoria por las leídas (las que otro proceso borró desaparecen y las nuevas
    /// aparecen) y la vuelve a seguir como <c>Unchanged</c> con los valores de la cabecera recargados. Así un guardado posterior no
    /// reinserta filas borradas ni confunde filas existentes con nuevas.
    /// </summary>
    public async Task RecargarAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        contexto.ChangeTracker.Clear();
        var id = cotizacion.Id;
        var leida = await LeerAgregadoAsync(
                () => ConAgregado().AsNoTrackingWithIdentityResolution().FirstOrDefaultAsync(c => c.Id == id, cancellationToken), cancellationToken)
            ?? throw new InvalidOperationException($"La cotización {cotizacion.Folio} ya no existe en la base.");
        cotizacion.ReemplazarRutasLeidas(leida);
        contexto.Attach(cotizacion);
        await contexto.Entry(cotizacion).ReloadAsync(cancellationToken);
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

    public Task<IReadOnlyList<clsCotizacionConLinea>> ListarConLineaPorUsuarioAsync(
        Guid usuarioId, int limite = IClsCotizacionRepository.LimitePorDefecto, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limite, 1);
        return ListarConLineaAsync(c => c.UsuarioId == usuarioId, limite, cancellationToken);
    }

    public async Task<IReadOnlyList<clsCotizacionConLinea>> ListarPorUsuariosAsync(
        IReadOnlyCollection<Guid> usuarioIds, int limite = IClsCotizacionRepository.LimitePorDefecto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuarioIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(limite, 1);
        List<Guid?> ids = [.. usuarioIds.Distinct().Select(id => (Guid?)id)];
        // HU-037.3 (#211): el director sin regional pide los de TODOS los usuarios. EF.Parameter manda la lista como un solo parámetro
        // (OPENJSON), sin el tope de 2100 parámetros de SQL Server que tendría una lista expandida.
        return ids.Count == 0 ? [] : await ListarConLineaAsync(c => EF.Parameter(ids).Contains(c.UsuarioId), limite, cancellationToken);
    }

    /// <summary>
    /// Proyección de "Mis cotizaciones" (y del listado regional, HU-016.1): cabeceras que cumplen <paramref name="filtro"/>, de la más
    /// reciente a la más antigua, con la línea de negocio y el margen de su ruta 1 (o "Varias").
    /// </summary>
    private async Task<IReadOnlyList<clsCotizacionConLinea>> ListarConLineaAsync(
        Expression<Func<clsCotizacionSolicitud, bool>> filtro, int limite, CancellationToken cancellationToken)
    {
        var cabeceras = await CabecerasToleranteAsync(filtro, limite, cancellationToken);
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

            // Una ruta sin línea de negocio no cuenta para "Varias" (HU-021.9).
            var primera = suyas[0];
            var conLinea = suyas.Select(r => r.LineaNegocioId).OfType<int>().Distinct().ToList();
            var linea = conLinea.Count > 1
                ? clsCotizacionConLinea.Varias
                : conLinea.Count == 1 ? lineasNegocio.GetValueOrDefault(conLinea[0]) : null;
            return new clsCotizacionConLinea(c, linea, primera.MargenAplicado, primera.OrigenMargen);
        })];
    }

    /// <summary>
    /// Cabeceras que cumplen <paramref name="filtro"/> (HU-014.1, mediano plazo): si una fila trae un código fuera de catálogo (el
    /// convertidor del enum la rechaza), en vez de tumbar toda la lista se leen una por una y se omiten las inválidas (queda en el log).
    /// </summary>
    private async Task<List<clsCotizacionSolicitud>> CabecerasToleranteAsync(
        Expression<Func<clsCotizacionSolicitud, bool>> filtro, int limite, CancellationToken cancellationToken)
    {
        var consulta = contexto.CotizacionesSolicitud.AsNoTracking()
            .Where(filtro)
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
                    _logger.LogWarning(porFila, "La cotización {Id} tiene un valor fuera de catálogo: se omite del listado.", id);
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
