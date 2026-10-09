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
        IClsPrecioCosteoService precioCosteo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(precioCosteo);

        // HU-021: primero los camiones (estado, flete y retorno), después la solicitud resume a sus rutas (§9.2) y por último el precio.
        var cambio = precioCosteo.ActualizarCamiones(cotizacion, foto, ahora);

        // Conexos que Nexo agregó y Cotifly no pudo leer en su momento (HU-013.15): se traen a la ruta 1 y se quita la marca.
        var importarConexos = cotizacion.ConexosPendientesDeLeer;
        if (importarConexos && cotizacion.PdfPath is null && foto.Cotizacion is { } nexo && cotizacion.PrimeraRuta is { } ruta)
        {
            var faltantes = nexo.Conexos.Where(c => ruta.Conexos.All(r => !string.Equals(r.IdServicioConexo, c.Id, StringComparison.OrdinalIgnoreCase)));
            var importados = clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta, faltantes, ahora);
            cambio |= importados.EsFallo || importados.Valor > 0;
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

    /// <summary>Motivo de una entregada leída sin precio y sin otra explicación (Nexo no trajo la cotización o sus fletes).</summary>
    public const string MotivoEntregadaSinPrecio =
        "Nexo entregó la cotización pero no devolvió su costeo (cotización o fletes): no se calcula el precio. Revísala con el equipo de Nexo.";
}
