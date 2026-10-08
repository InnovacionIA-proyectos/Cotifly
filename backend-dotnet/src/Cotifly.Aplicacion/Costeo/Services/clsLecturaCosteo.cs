using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Aplica una foto del costeo de Nexo a la cotización con la regla del polling (HU-005.6 y HU-005.7): deriva <c>estado_costeo</c>,
/// guarda el id de la cotización de Nexo, reprograma la agenda (o la detiene si el estado es terminal) y pone el precio con el margen
/// guardado, también línea por línea (ADR-017). La usan el polling, el envío cuando Nexo ya costeó en la misma petición y el resultado
/// ganada/perdida cuando el costeo aún no estaba aplicado (HU-013.14, ADR-018 §17): las tres lecturas dan el mismo resultado.
/// </summary>
public static class clsLecturaCosteo
{
    /// <summary>
    /// Aplica <paramref name="foto"/> y devuelve <c>true</c> si cambió algo que ve el comercial (estado, costo o precio): entonces el
    /// llamador sube la versión de negocio. No guarda.
    /// </summary>
    public static bool AplicarFoto(
        clsCotizacion cotizacion,
        IReadOnlyList<clsCotizacionLineaSolicitud> lineas,
        clsCosteoConsultadoNexo foto,
        DateTimeOffset ahora,
        clsOpcionesCosteoNexo opciones,
        IClsPrecioCosteoService precioCosteo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(foto);
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(precioCosteo);

        var estado = clsDerivacionEstadoCosteo.Derivar(foto);
        var siguiente = clsDerivacionEstadoCosteo.EsTerminal(estado)
            ? null
            : cotizacion.CosteoEnviadoAt is { } enviadaAt
                ? clsAgendaPollingCosteo.Siguiente(ahora, enviadaAt, 0, opciones)
                : ahora + opciones.IntervaloInicial;
        var cambio = cotizacion.RegistrarConsultaCosteo(estado, foto.Cotizacion?.Id, ahora, siguiente);
        return precioCosteo.Aplicar(cotizacion, lineas, foto, ahora) | cambio;
    }
}
