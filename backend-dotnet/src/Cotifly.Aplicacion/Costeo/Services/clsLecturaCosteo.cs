using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Aplica una foto del costeo de Nexo a la cotización con la regla del polling (HU-005.6 y HU-005.7): deriva <c>estado_costeo</c>,
/// guarda el id de la cotización de Nexo, reprograma la agenda (o la detiene si el estado es terminal) y pone el precio con el margen
/// guardado, camión por camión y ruta por ruta (ADR-017, HU-021). La usan el polling, el envío cuando Nexo ya costeó en la misma petición y el resultado
/// ganada/perdida cuando el costeo aún no estaba aplicado (HU-013.14, ADR-018 §17): las tres lecturas dan el mismo resultado.
/// </summary>
public static class clsLecturaCosteo
{
    /// <summary>
    /// Aplica <paramref name="foto"/> y devuelve <c>true</c> si cambió algo que ve el comercial (estado, costo o precio): entonces el
    /// llamador sube la versión de negocio. No guarda.
    /// </summary>
    public static bool AplicarFoto(
        clsCotizacionSolicitud cotizacion,
        clsCosteoConsultadoNexo foto,
        DateTimeOffset ahora,
        clsOpcionesCosteoNexo opciones,
        IClsPrecioCosteoService precioCosteo,
        clsConexosPendientesNexo? pendientes = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(precioCosteo);

        // HU-021: primero los camiones (estado, flete y retorno), después la solicitud resume a sus rutas (§9.2) y por último el precio.
        var cambio = precioCosteo.ActualizarCamiones(cotizacion, foto, ahora);

        // Conexos que Nexo agregó y Cotifly no pudo leer en su momento (HU-013.15): solo los nuevos del registro de pendientes (#135), cada
        // uno a su ruta (la del camión cuya línea los lleva, #134; si no se sabe, la ruta pedida) y se quita la marca. Fuera de esa marca el
        // polling no importa conexos (decisión 3).
        var importarConexos = cotizacion.ConexosPendientesDeLeer;
        if (importarConexos && cotizacion.PdfPath is null && foto.Cotizacion is { } nexo)
        {
            var nuevos = ConexosNuevos(cotizacion, nexo, pendientes);
            if (nuevos.Ruta is { } ruta && nuevos.Conexos.Count > 0)
            {
                var importados = clsServiciosConexosCosteoService.ImportarConexosPorRuta(cotizacion, nexo, nuevos.Conexos, ruta, ahora);
                cambio |= importados.EsFallo || importados.Valor > 0;
            }
        }

        var estadoNexo = clsDerivacionEstadoCosteo.Derivar(foto);
        var estado = cotizacion.Rutas.Count == 0 ? estadoNexo : cotizacion.ResumirRutas() ?? estadoNexo;
        var siguiente = clsDerivacionEstadoCosteo.EsTerminal(estado)
            ? null
            : cotizacion.CosteoEnviadoAt is { } enviadaAt
                ? clsAgendaPollingCosteo.Siguiente(ahora, enviadaAt, 0, opciones)
                : ahora + opciones.IntervaloInicial;
        cambio = cotizacion.RegistrarConsultaCosteo(estadoNexo, foto.Cotizacion?.Id, siguiente) | cambio;
        if (importarConexos && foto.Cotizacion is not null)
        {
            cotizacion.QuitarMarcaConexosPendientes();
        }

        cambio = precioCosteo.AplicarPrecio(cotizacion, foto, ahora) | cambio;

        // Una entregada que se leyó y sigue sin precio (p. ej. Nexo no trajo la cotización) queda con motivo: así no parece "sin leer" y
        // la reparación del arranque no la vuelve a agendar (ADR-018 §17).
        if (cotizacion.EstadoCosteo == EstadoCosteo.Entregada && cotizacion.EntregadaSinPrecio && cotizacion.MotivoEstadoCosteo is null)
        {
            cotizacion.AdvertirCosteo(MotivoEntregadaSinPrecio);
            cambio = true;
        }

        return cambio;
    }

    /// <summary>
    /// Los conexos de la cotización de Nexo que se importan con la marca de pendientes (HU-021.8, #135): solo los que NO estaban en Nexo
    /// antes del POST incierto (<see cref="clsConexosPendientesNexo.Previos"/>) ni están ya en alguna ruta, y van a la ruta pedida. Un
    /// conexo que el comercial quitó en Cotifly sigue en Nexo (no hay DELETE) pero estaba en los previos: no vuelve. Sin el registro de
    /// pendientes (o si la ruta ya no existe) no se importa nada.
    /// </summary>
    public static (clsLineaSolicitud? Ruta, IReadOnlyList<clsConexoNexo> Conexos) ConexosNuevos(
        clsCotizacionSolicitud cotizacion, clsCotizacionNexo nexo, clsConexosPendientesNexo? pendientes)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(nexo);
        var ruta = pendientes is null ? null : cotizacion.Rutas.FirstOrDefault(r => r.Id == pendientes.LineaSolicitudId);
        if (ruta is null)
        {
            return (null, []);
        }

        var conocidos = pendientes!.Previos
            .Concat(cotizacion.Rutas.SelectMany(r => r.Conexos).Select(c => c.IdServicioConexo))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Decisión 10 (HU-021.9): un conexo que Cotifly envió por su cuenta tiene en Nexo otro id de fila, pero su servicioConexoId es el
        // id que ya está en la ruta: tampoco se importa (no se duplica).
        return (ruta, [.. nexo.Conexos.Where(c => !conocidos.Contains(c.Id) && (c.ServicioConexoId is null || !conocidos.Contains(c.ServicioConexoId)))]);
    }

    /// <summary>
    /// #181: la situación que Nexo informa de la línea de cada camión vigente ya asociado (<c>estadoLinea</c> y <c>motivoPendiente</c> de
    /// <see cref="clsCosteoConsultadoNexo.Detalle"/>), por id de camión. Se llama después de <see cref="AplicarFoto"/> (que asocia los
    /// camiones); no toca la cotización ni el emparejamiento. Los camiones sin línea de Nexo, o cuya línea no trae nada (Nexo sin #309), no
    /// salen.
    /// </summary>
    public static IReadOnlyDictionary<Guid, clsSituacionLineaNexo> SituacionesPorCamion(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        var porLinea = foto.Detalle
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Situacion, StringComparer.OrdinalIgnoreCase);
        var situaciones = new Dictionary<Guid, clsSituacionLineaNexo>();
        foreach (var camion in clsPrecioCosteoService.Vigentes(cotizacion))
        {
            if (camion.LineaSolicitudNexoId is { } lineaId && porLinea.TryGetValue(lineaId, out var situacion) && !situacion.Vacia)
            {
                situaciones[camion.Id] = situacion;
            }
        }

        return situaciones;
    }

    /// <summary>Motivo de una entregada leída sin precio y sin otra explicación (Nexo no trajo la cotización o sus fletes).</summary>
    public const string MotivoEntregadaSinPrecio =
        "Nexo entregó la cotización pero no devolvió su costeo (cotización o fletes): no se calcula el precio. Revísala con el equipo de Nexo.";
}
