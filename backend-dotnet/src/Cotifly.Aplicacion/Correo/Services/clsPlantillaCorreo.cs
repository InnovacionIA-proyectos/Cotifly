using System.Globalization;
using System.Net;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Plantilla del correo portada del Python: asunto por defecto de <c>enviar_cotizacion</c> (<c>orquestador/web.py</c>), cuerpo
/// <c>_PLANTILLA</c> y envoltura HTML corporativa <c>_cuerpo_html</c> (<c>mcp_server/correo.py</c>). HU-033 (#199, ADR-023, decisión 5):
/// después del texto del comercial va un bloque FIJO con el precio de cada ruta y el VALOR TOTAL de la cotización, sin desglose (nunca
/// costo, margen, flete ni conexos con precio), con la misma leyenda de impuestos del PDF. El correo viaja en HTML (MailSender envía
/// una sola parte: <c>IsHtml=true</c>).
/// </summary>
public static class clsPlantillaCorreo
{
    /// <summary>Cuerpo por defecto (<c>_PLANTILLA</c>).</summary>
    public const string CuerpoPorDefecto =
        "Estimado cliente,\n\n"
        + "Adjuntamos la cotización solicitada. Quedamos atentos a cualquier inquietud.\n\n"
        + "Cordialmente,\nEquipo Comercial — Botero Soto";

    /// <summary>Título del bloque del resumen.</summary>
    public const string TituloResumen = "Resumen de la cotización";

    /// <summary>Etiqueta del total del bloque del resumen (la misma del PDF).</summary>
    public const string EtiquetaTotal = clsTextosEstandar.EtiquetaTotalGeneral;

    /// <summary>Asunto por defecto: <c>Cotización {folio} — Botero Soto</c>.</summary>
    public static string AsuntoPorDefecto(string folio) => $"Cotización {folio} — Botero Soto";

    /// <summary>
    /// Resumen para el cliente: una fila por ruta, en orden, con su precio final (transporte y conexos incluidos, <c>precioFinalLinea</c>) y
    /// el total (<c>precioFinal</c>, la suma). Una ruta que rechazó la Mesa de Costeo sale "Sin costeo" y no suma (HU-028.1). <c>null</c> si
    /// la cotización todavía no tiene total.
    /// </summary>
    public static clsResumenCorreo? Resumen(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (cotizacion.PrecioFinal is not { } total)
        {
            return null;
        }

        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;
        var rutas = cotizacion.RutasOrdenadas
            .Select(r => new clsRutaResumenCorreo(
                r.Orden,
                clsTextoSeguro.LimpiarLinea($"{r.OrigenNombre} → {r.DestinoNombre}") ?? $"Ruta {r.Orden.ToString(CultureInfo.InvariantCulture)}",
                r.Rechazada ? null : r.PrecioFinalLinea,
                r.Rechazada || r.PrecioFinalLinea is null ? Costeo.Services.clsCosteoCotizacionService.TextoSinCosteo : clsFormatoMoneda.Formatear(r.PrecioFinalLinea, moneda)))
            .ToList();
        return new clsResumenCorreo(
            moneda.ToUpperInvariant(),
            total,
            clsFormatoMoneda.Formatear(total, moneda),
            rutas,
            clsTextosEstandar.LeyendaImpuestos,
            string.Equals(moneda.Trim(), "USD", StringComparison.OrdinalIgnoreCase) && cotizacion.TrmAplicada > 0 ? clsTextosEstandar.NotaTrmMinima(cotizacion.TrmAplicada) : null);
    }

    /// <summary>
    /// Cuerpo en HTML: el aviso de pruebas (si el correo se redirige), el texto escapado (un <c>&lt;script&gt;</c> del comercial llega como
    /// texto) con los saltos de línea como <c>&lt;br&gt;</c>, el resumen con el total (si hay) y la firma corporativa en azul Botero Soto.
    /// </summary>
    public static string Html(string texto, clsResumenCorreo? resumen = null, string? avisoPruebas = null)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var cuerpo = Escapar(texto);
        return "<div style=\"font-family:Arial,Helvetica,sans-serif;color:#1A2A3A;font-size:14px;line-height:1.5\">"
               + (avisoPruebas is null ? string.Empty : AvisoHtml(avisoPruebas))
               + cuerpo
               + (resumen is null ? string.Empty : ResumenHtml(resumen))
               + "<hr style=\"border:none;border-top:1px solid #D9DEE5;margin:16px 0\">"
               + "<div style=\"font-size:12px;color:#5F6B7A\">"
               + "<b style=\"color:#005AA8\">Botero Soto</b> · Soluciones Logísticas</div>"
               + "</div>";
    }

    /// <summary>Recuadro del aviso de pruebas (correo redirigido) al inicio del cuerpo.</summary>
    public static string AvisoHtml(string aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return "<div style=\"background:#FFF4E5;border:1px solid #F0A040;border-radius:4px;padding:8px 12px;margin:0 0 16px 0;font-size:12px\">"
               + Escapar(aviso)
               + "</div>";
    }

    /// <summary>Bloque fijo del resumen: tabla de rutas con su precio y el total, con la leyenda de impuestos (y la de TRM en USD).</summary>
    public static string ResumenHtml(clsResumenCorreo resumen)
    {
        ArgumentNullException.ThrowIfNull(resumen);
        var filas = string.Concat(resumen.Rutas.Select(r =>
            "<tr><td style=\"padding:4px 8px;border-bottom:1px solid #D9DEE5\">"
            + WebUtility.HtmlEncode($"Ruta {r.Orden.ToString(CultureInfo.InvariantCulture)}: {r.Ruta}")
            + "</td><td style=\"padding:4px 8px;border-bottom:1px solid #D9DEE5;text-align:right;white-space:nowrap\">"
            + WebUtility.HtmlEncode(r.PrecioTexto)
            + "</td></tr>"));
        return "<div style=\"margin:16px 0 0 0\">"
               + $"<div style=\"font-weight:bold;color:#005AA8;margin-bottom:6px\">{WebUtility.HtmlEncode(TituloResumen)}</div>"
               + "<table style=\"border-collapse:collapse;font-size:13px;min-width:320px\">"
               + filas
               + "<tr><td style=\"padding:6px 8px;font-weight:bold\">" + WebUtility.HtmlEncode(EtiquetaTotal) + "</td>"
               + "<td style=\"padding:6px 8px;font-weight:bold;color:#005AA8;text-align:right;white-space:nowrap\">"
               + WebUtility.HtmlEncode($"{resumen.TotalTexto} {resumen.Moneda}") + "</td></tr>"
               + "</table>"
               + $"<div style=\"font-size:12px;color:#5F6B7A;margin-top:6px\">{WebUtility.HtmlEncode(resumen.Leyenda)}</div>"
               + (resumen.NotaMoneda is { } nota ? $"<div style=\"font-size:12px;color:#5F6B7A\">{WebUtility.HtmlEncode(nota)}</div>" : string.Empty)
               + "</div>";
    }

    private static string Escapar(string texto) =>
        WebUtility.HtmlEncode(texto.Replace("\r\n", "\n", StringComparison.Ordinal)).Replace("\n", "<br>", StringComparison.Ordinal);
}

/// <summary>
/// HU-033: resumen de la cotización para el cliente en el correo y en la vista previa (sin desglose).
/// </summary>
/// <param name="Moneda"><c>COP</c> o <c>USD</c>.</param>
/// <param name="Total">Valor total (<c>precioFinal</c>, la suma de las rutas con precio).</param>
/// <param name="TotalTexto">Total con el formato del PDF (<c>$3.294.798</c>, <c>US$996,35</c>).</param>
/// <param name="Rutas">Precio final de cada ruta, en orden.</param>
/// <param name="Leyenda">Leyenda de impuestos (la del PDF).</param>
/// <param name="NotaMoneda">En USD, la nota de la TRM mínima del PDF; <c>null</c> en COP.</param>
public sealed record clsResumenCorreo(
    string Moneda, decimal Total, string TotalTexto, IReadOnlyList<clsRutaResumenCorreo> Rutas, string Leyenda, string? NotaMoneda);

/// <summary>Una ruta del resumen: su orden, "Origen → Destino" y su precio final (<c>null</c> si la Mesa la rechazó).</summary>
public sealed record clsRutaResumenCorreo(int Orden, string Ruta, decimal? Precio, string PrecioTexto);
