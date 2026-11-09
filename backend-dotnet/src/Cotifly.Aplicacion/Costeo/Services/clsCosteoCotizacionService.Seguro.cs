using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9): el seguro de la mercancía en el PDF del costeo. La política NOGE0076 pide mostrarlo aparte del flete: la
/// fila de la ruta imprime su precio sin el seguro (<see cref="PrecioRutaPdf"/>) y una fila propia, "Seguro de la mercancía", su valor en la
/// moneda de la cotización. El total general no cambia (la suma de los precios finales, con el seguro). Nunca se imprime la tasa.
/// </summary>
public sealed partial class clsCosteoCotizacionService
{
    /// <summary>Texto de la fila del seguro en la tabla de la ruta y en el resumen del pliego.</summary>
    public const string EtiquetaSeguroPdf = "Seguro de la mercancía";

    /// <summary>Nota de la fila del seguro en la tabla de la ruta.</summary>
    public const string NotaSeguroPdf = "Póliza de la mercancía transportada; se cobra aparte del flete.";

    /// <summary>Frase que se agrega a la nota del resumen del pliego cuando alguna ruta incluye el seguro.</summary>
    public const string NotaResumenSeguroPdf = "El seguro de la mercancía de cada ruta va en su propia fila y también suma en el total.";

    /// <summary>
    /// El seguro de la mercancía de la ruta en la moneda de la cotización (<see cref="clsLineaSolicitud.ValorSeguro"/>); <c>null</c> si la
    /// ruta no lo incluye, no tiene valor calculado (sin valor de la mercancía) o la Mesa de Costeo la rechazó.
    /// </summary>
    public static decimal? ValorSeguroPdf(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        return !ruta.Rechazada && ruta.ValorSeguro(cotizacion.Moneda, cotizacion.TrmAplicada, out var valor) is null ? valor : null;
    }

    /// <summary>
    /// Agrega la fila "Seguro de la mercancía" a la tabla de cada ruta que lo incluye. Las secciones del documento siguen el orden de las
    /// rutas (una en el PDF de una ruta, una por ruta en el pliego), igual que <see cref="Payload"/> y <see cref="PayloadPliego"/>.
    /// </summary>
    public static clsDocumentoCotizacion ConFilasSeguro(clsDocumentoCotizacion documento, clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(documento);
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        if (documento.Secciones.Count != rutas.Count || !rutas.Any(r => ValorSeguroPdf(cotizacion, r) is not null))
        {
            return documento;
        }

        var secciones = documento.Secciones
            .Select((seccion, i) => ValorSeguroPdf(cotizacion, rutas[i]) is { } seguro
                ? seccion with
                {
                    Filas =
                    [
                        .. seccion.Filas,
                        new clsFilaDocumento(
                            clsTextoSeguro.PartirPalabrasLargas(EtiquetaSeguroPdf, clsConstructorDocumentoCotizacion.TramoDestino),
                            string.Empty,
                            clsFormatoMoneda.Formatear(seguro, moneda),
                            NotaSeguroPdf),
                    ],
                }
                : seccion)
            .ToList();
        return documento with { Secciones = secciones };
    }
}
