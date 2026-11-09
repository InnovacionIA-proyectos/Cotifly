namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9): el seguro de la mercancía al entregar la cotización. Una ruta con el seguro marcado pero sin valor de la
/// mercancía (la edición quitó el valor: el check queda sin tasa ni costo) no suma el seguro al precio. Decisión del usuario del
/// 2026-11-09: se avisa (la vista del costeo y la respuesta del PDF), no se bloquea.
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>
    /// Las rutas que cotizan (las rechazadas por la Mesa no, HU-028.1) con el seguro marcado y sin seguro calculado (sin valor de la
    /// mercancía), en orden. Es un método y no una propiedad para que EF Core no la tome por una navegación.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitud> RutasConSeguroSinValor() => [.. RutasCotizables.Where(r => r.IncluyeSeguro && r.CostoSeguro is null)];
}
