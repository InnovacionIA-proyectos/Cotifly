namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// El seguro de la mercancía al entregar la cotización (ADR-032 §9). Una ruta con el seguro marcado pero sin valor de la
/// mercancía (la edición quitó el valor: el check queda sin tasa ni costo) no suma el seguro al precio. Se avisa (la vista del costeo y
/// la respuesta del PDF), no se bloquea.
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>
    /// Las rutas que cotizan (las rechazadas por la Mesa no) con el seguro marcado y sin seguro calculado (sin valor de la
    /// mercancía), en orden. Es un método y no una propiedad para que EF Core no la tome por una navegación.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitud> RutasConSeguroSinValor() => [.. RutasCotizables.Where(r => r.IncluyeSeguro && r.CostoSeguro is null)];
}
