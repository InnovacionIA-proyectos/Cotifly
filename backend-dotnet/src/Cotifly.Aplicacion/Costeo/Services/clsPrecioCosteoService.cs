using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Costeo de Nexo aplicado al agregado de la solicitud (HU-005.6, HU-005.7, ADR-015 §5; HU-021, ADR-019): Nexo cotiza cada CAMIÓN como
/// una línea de su solicitud; Cotifly guarda en cada camión su estado, su flete y su retorno, y la ruta suma, aplica el margen de su
/// línea de negocio y saca su precio (las reglas de totales viven en el dominio: <see cref="clsLineaSolicitud.RecalcularTotales"/> y
/// <see cref="clsCotizacionSolicitud.RecalcularTotales"/>). El costo de Nexo es interno: el comercial ve el precio. En USD (ADR-014 §5)
/// cada precio se calcula en pesos y se convierte con la <c>trmAplicada</c> fijada en la solicitud. El LLM nunca pone números (ADR-003).
/// </summary>
public interface IClsPrecioCosteoService
{
    /// <summary>
    /// Primera parte de una lectura de Nexo: asocia cada camión sin id con su línea de Nexo (por contenido), y le registra el estado
    /// derivado y, si la solicitud no tiene PDF, el flete y el retorno; cada ruta resume el estado de sus camiones. Con PDF solo cambian
    /// los estados (el precio de la versión se conserva, ADR-015 §5). Devuelve <c>true</c> si algo cambió.
    /// </summary>
    bool ActualizarCamiones(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora);

    /// <summary>
    /// Segunda parte (después de registrar el estado de la solicitud): recalcula los totales y el precio de cada ruta y de la solicitud
    /// (§9.1) y deja en <c>motivoEstadoCosteo</c> lo que impide el precio (camiones sin asociar, sin flete, sin margen, costo negativo o
    /// el costeo cambiado después del PDF). Devuelve <c>true</c> si el precio o los totales cambiaron.
    /// </summary>
    bool AplicarPrecio(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora);
}

/// <summary>
/// Implementación de <see cref="IClsPrecioCosteoService"/>. Deja en el log (warning) las líneas de Nexo sin dueño, los empates que no se
/// resolvieron y los camiones cuya línea borró la Mesa (#134).
/// </summary>
public sealed class clsPrecioCosteoService(ILogger<clsPrecioCosteoService>? logger = null) : IClsPrecioCosteoService
{
    private readonly ILogger _logger = logger ?? NullLogger<clsPrecioCosteoService>.Instance;

    public const string MotivoSinMargen =
        "Nexo ya costeó la solicitud, pero la cotización no tiene margen resuelto: no se calcula el precio. Reenvíala o revisa la línea de negocio.";

    public const string MotivoCambioDespuesDelPdf =
        "El costeo de Nexo cambió después de generar el PDF: el precio de esta versión se conserva. Genera una versión nueva para cotizar con el costeo actual.";

    /// <summary>Motivo cuando hay camiones que no se pudieron asociar con certeza a las líneas de Nexo (quedan sin precio y sin total).</summary>
    public static string MotivoSinAsociar(int sinAsociar, int total) =>
        $"{sinAsociar} de {total} líneas no se pudieron asociar con certeza a las de Nexo (ruta, peso o vehículo distintos, o líneas idénticas): "
        + "quedan sin precio y la licitación sin total. Revísalas con el equipo de Nexo.";

    /// <summary>
    /// Asigna a cada camión sin id su línea de Nexo por CONTENIDO (<see cref="clsEmparejadorLineasNexo"/>; ADR-017 §3: Nexo no garantiza
    /// el orden de <c>solicitud.lineas[]</c>), con los empates de camiones idénticos de una ruta resueltos en el orden de envío (#134).
    /// Con un camión en Cotifly y una línea en Nexo se asocian directamente, como en HU-005.5. Los camiones <c>descartada</c> no
    /// participan (regla del núcleo, ADR-019). Devuelve cuántos camiones vigentes siguen sin asociar.
    /// </summary>
    public static int AsociarCamionesNexo(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto) => Asociar(cotizacion, foto).SinAsociar;

    /// <summary><see cref="AsociarCamionesNexo"/> con el detalle del emparejamiento (<c>null</c> si no hizo falta el emparejador).</summary>
    private static (int SinAsociar, clsEmparejamiento? Detalle) Asociar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);

        // En el orden en que viajaron a Nexo (ruta por ruta, camión por camión): es el orden estable de los empates.
        var camiones = Vigentes(cotizacion);
        var pendientes = camiones.Where(c => c.LineaSolicitudNexoId is null).ToList();
        if (pendientes.Count == 0 || foto.LineasSolicitud.Count == 0)
        {
            return (pendientes.Count, null);
        }

        if (camiones.Count == 1 && foto.LineasSolicitud.Count == 1)
        {
            cotizacion.AsignarLineaNexo(camiones[0].Id, foto.LineasSolicitud[0]);
            return (0, null);
        }

        var porCamion = cotizacion.RutasOrdenadas.SelectMany(r => r.Vehiculos.Select(v => (Ruta: r, Camion: v))).ToDictionary(x => x.Camion.Id);
        var asignados = cotizacion.Vehiculos.Select(c => c.LineaSolicitudNexoId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detalle = foto.Detalle.Where(d => foto.LineasSolicitud.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var candidatos = pendientes.Select(c => clsCamionParaEmparejar.Desde(porCamion[c.Id].Ruta, c)).ToList();
        var emparejamiento = clsEmparejadorLineasNexo.EmparejarConDetalle(candidatos, detalle, asignados);
        foreach (var (camionId, nexoId) in emparejamiento.Pares)
        {
            cotizacion.AsignarLineaNexo(camionId, nexoId);
        }

        return (camiones.Count(c => c.LineaSolicitudNexoId is null), emparejamiento);
    }

    /// <summary>
    /// Camiones cuya línea borró la Mesa en Nexo (#134): tenían <c>lineaSolicitudNexoId</c> y esa línea ya no está en la solicitud. Pasan a
    /// <c>descartada</c> (sin línea ni costos de Nexo): así no quedan para siempre en <c>pendiente_costeo</c>, no frenan el precio de su
    /// ruta y los conexos van al siguiente camión. Solo con una foto que trae líneas y una solicitud no descartada. Devuelve esos camiones.
    /// </summary>
    public static IReadOnlyList<clsVehiculoLineaSolicitud> CamionesConLineaBorrada(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        if (foto.LineasSolicitud.Count == 0
            || string.Equals(foto.EstadoSolicitud, clsCosteoConsultadoNexo.SolicitudDescartada, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var vigentes = foto.LineasSolicitud.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. cotizacion.Vehiculos.Where(c => c.LineaSolicitudNexoId is { } id && !vigentes.Contains(id))];
    }

    public bool ActualizarCamiones(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        var borradas = false;
        foreach (var camion in CamionesConLineaBorrada(cotizacion, foto))
        {
            _logger.LogWarning("La línea {Linea} de Nexo del camión {Camion} (cotización {Folio}) ya no está en la solicitud: el camión queda descartado.",
                camion.LineaSolicitudNexoId, camion.Id, cotizacion.Folio);
            borradas |= cotizacion.DescartarCamionSinLineaEnNexo(camion.Id);
        }

        var (_, emparejamiento) = Asociar(cotizacion, foto);
        if (emparejamiento is not null)
        {
            Avisar(cotizacion, emparejamiento);
        }

        // Con PDF el precio de la versión se conserva: los camiones solo actualizan su estado (ADR-015 §5). La raíz aplica la lectura,
        // resume las rutas y recalcula los totales (ADR-019: nadie cambia los camiones por fuera del agregado). Un camión descartado no
        // se lee (regla del núcleo): sigue descartado, sin línea de Nexo, y no cuenta en el estado ni en los totales de su ruta.
        var conPdf = cotizacion.PdfPath is not null;
        var lecturas = Vigentes(cotizacion).Select(camion =>
        {
            var estado = clsDerivacionEstadoCosteo.DerivarLinea(foto, camion.LineaSolicitudNexoId);
            if (conPdf)
            {
                return new clsCosteoCamionNexo(camion.Id, estado, ConCostos: false);
            }

            var costos = clsDerivacionEstadoCosteo.CostosCamion(foto, camion.LineaSolicitudNexoId);
            if (costos is { } c && (c.Flete < 0m || c.Retorno < 0m))
            {
                // Un costo negativo de Nexo no se usa (ni rompe el polling): el camión queda sin flete y se advierte en AplicarPrecio.
                costos = null;
            }

            // La Mesa de Costeo le puso vehículo a una línea que iba vacía (sin selección automática): se copia.
            var linea = Detalle(foto, camion.LineaSolicitudNexoId);
            return new clsCosteoCamionNexo(camion.Id, estado, ConCostos: true, costos?.Flete, costos?.Retorno, linea?.TipoVehiculo, linea?.Carroceria);
        }).ToList();

        // Un error del módulo de precio lo reporta AplicarPrecio (que vuelve a recalcular): aquí cuenta como cambio.
        var aplicado = cotizacion.AplicarFotoNexo(lecturas, ahora);
        return borradas || aplicado.EsFallo || aplicado.Valor;
    }

    public bool AplicarPrecio(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        var camiones = Vigentes(cotizacion);
        var varios = camiones.Count > 1;
        var sinAsociar = camiones.Count(c => c.LineaSolicitudNexoId is null);
        if (cotizacion.PdfPath is not null)
        {
            if (CambioDespuesDelPdf(cotizacion, foto))
            {
                cotizacion.AdvertirCosteo(MotivoCambioDespuesDelPdf);
            }

            return false;
        }

        var recalculo = cotizacion.RecalcularTotales(ahora);
        if (recalculo.EsFallo)
        {
            cotizacion.AdvertirCosteo($"No se pudo calcular el precio sobre el costeo de Nexo: {recalculo.Error.Mensaje}");
            return true;
        }

        var cambio = recalculo.Valor;
        if (camiones.Select(c => clsDerivacionEstadoCosteo.CostosCamion(foto, c.LineaSolicitudNexoId)).FirstOrDefault(c => c is { } x && (x.Flete < 0m || x.Retorno < 0m)) is not null)
        {
            var orden = cotizacion.RutasOrdenadas.First(r => r.Vehiculos.Any(v => clsDerivacionEstadoCosteo.CostosCamion(foto, v.LineaSolicitudNexoId) is { } x && (x.Flete < 0m || x.Retorno < 0m))).Orden;
            cotizacion.AdvertirCosteo($"Nexo devolvió un costo negativo para la línea {orden}: no se calcula su precio. Revísala con el equipo de Nexo.");
            return cambio;
        }

        if (varios && sinAsociar > 0 && foto.LineasSolicitud.Count > 0)
        {
            cotizacion.AdvertirCosteo(MotivoSinAsociar(sinAsociar, camiones.Count));
            return cambio;
        }

        if (cotizacion.EstadoCosteo is not { } estado || !clsDerivacionEstadoCosteo.TieneCosteo(estado) || foto.Cotizacion is null)
        {
            return cambio;
        }

        if (varios && camiones.Count(c => c.CostoFlete is null) is > 0 and var sinFlete)
        {
            cotizacion.AdvertirCosteo(
                $"Nexo entregó la cotización sin flete para {sinFlete} de {camiones.Count} líneas: no se calcula el precio total. Revísala con el equipo de Nexo.");
            return cambio;
        }

        if (cotizacion.Rutas.Any(r => r.MargenAplicado is null || r.MetodoCalculo is null))
        {
            cotizacion.AdvertirCosteo(MotivoSinMargen);
        }

        return cambio;
    }

    /// <summary>¿El flete o el retorno de Nexo de algún camión difieren de los guardados? (la solicitud ya tiene PDF).</summary>
    private static bool CambioDespuesDelPdf(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto) =>
        foto.Cotizacion is not null
        && cotizacion.Vehiculos.Any(c => clsDerivacionEstadoCosteo.CostosCamion(foto, c.LineaSolicitudNexoId) is { } costos
                                         && (costos.Flete != c.CostoFlete || costos.Retorno != (c.CostoRetornoContenedor ?? 0m)));

    /// <summary>Una línea de warning por cada línea de Nexo sin dueño y por cada empate sin resolver (#134).</summary>
    private void Avisar(clsCotizacionSolicitud cotizacion, clsEmparejamiento emparejamiento)
    {
        foreach (var linea in emparejamiento.LineasSinDueno)
        {
            _logger.LogWarning("La línea {Linea} de la solicitud de Nexo de la cotización {Folio} quedó sin camión de Cotifly.", linea, cotizacion.Folio);
        }

        foreach (var empate in emparejamiento.EmpatesSinResolver)
        {
            _logger.LogWarning("Empate sin resolver en la cotización {Folio}: {Empate}. Quedan sin asociar para no cruzar costos entre rutas.",
                cotizacion.Folio, empate);
        }
    }

    /// <summary>
    /// Camiones que lee el polling, en el orden de envío: todos menos los <c>descartada</c> (regla del núcleo, ADR-019), salvo que todos lo
    /// estén (entonces se leen todos, como cuentan en la ruta).
    /// </summary>
    public static IReadOnlyList<clsVehiculoLineaSolicitud> Vigentes(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var todos = cotizacion.Vehiculos;
        var vigentes = todos.Where(c => c.EstadoCosteo != EstadoCosteo.Descartada).ToList();
        return vigentes.Count > 0 ? vigentes : todos;
    }

    private static clsLineaSolicitudNexo? Detalle(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId) =>
        lineaSolicitudNexoId is null
            ? null
            : foto.Detalle.FirstOrDefault(d => string.Equals(d.Id, lineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase));
}
