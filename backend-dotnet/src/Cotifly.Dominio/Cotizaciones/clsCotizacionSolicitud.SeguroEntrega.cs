using System.Globalization;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9): el seguro de la mercancía al entregar la cotización. Una ruta con el seguro marcado pero sin valor de la
/// mercancía (la edición quitó el valor: el check queda sin tasa ni costo y no suma al precio) no deja generar el PDF ni enviar el correo,
/// para no cobrar de menos sin que nadie lo note. Pendiente de decisión del usuario (bloquear o solo avisar): el PDF y el envío llaman a
/// <see cref="ValidarSeguroAntesDe"/>; para pasarlo a aviso basta con dejar de llamarla y mostrar <see cref="RutasConSeguroSinValor"/>.
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>
    /// Las rutas que cotizan (las rechazadas por la Mesa no, HU-028.1) con el seguro marcado y sin seguro calculado (sin valor de la
    /// mercancía), en orden. Es un método y no una propiedad para que EF Core no la tome por una navegación.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitud> RutasConSeguroSinValor() => [.. RutasCotizables.Where(r => r.IncluyeSeguro && r.CostoSeguro is null)];

    /// <summary>
    /// <c>SEGURO_SIN_VALOR_MERCANCIA</c> si alguna ruta tiene el seguro marcado sin valor de la mercancía (<see cref="RutasConSeguroSinValor"/>),
    /// con <c>detalle.rutas</c> (<c>linea_orden</c> y <c>linea_solicitud_id</c> de cada una); si no, <c>null</c>. <paramref name="accion"/>
    /// completa el mensaje ("generar el PDF", "enviarla al cliente").
    /// </summary>
    public clsErrorDominio? ValidarSeguroAntesDe(string accion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accion);
        var rutas = RutasConSeguroSinValor();
        if (rutas.Count == 0)
        {
            return null;
        }

        var ordenes = string.Join(", ", rutas.Select(r => r.Orden.ToString(CultureInfo.InvariantCulture)));
        var cuales = rutas.Count == 1 ? $"la ruta {ordenes}" : $"las rutas {ordenes}";
        return new clsErrorDominio(clsCodigosError.SeguroSinValorMercancia,
            $"La cotización {Folio} tiene el seguro de la mercancía marcado en {cuales} sin valor de la mercancía: su precio no incluye el "
            + $"seguro. Antes de {accion}, quita el seguro o agrega el valor de la mercancía (en una versión nueva si ya está finalizada).")
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["rutas"] = rutas
                    .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["linea_orden"] = r.Orden,
                        ["linea_solicitud_id"] = r.Id,
                    })
                    .ToList(),
            },
        };
    }
}
