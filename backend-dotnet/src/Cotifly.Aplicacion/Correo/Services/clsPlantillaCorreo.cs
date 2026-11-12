using System.Globalization;
using System.Net;
using Cotifly.Aplicacion.Comun.Interfaces;
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
    /// <summary>
    /// Cuerpo por defecto (<c>_PLANTILLA</c>). #262: sin la despedida; la pone el HTML al final, después del resumen y antes de la firma.
    /// </summary>
    public const string CuerpoPorDefecto =
        "Estimado cliente,\n\n"
        + "Adjuntamos la cotización solicitada. Quedamos atentos a cualquier inquietud.";

    /// <summary>#262: firma cuando no se conocen los datos del comercial.</summary>
    public const string FirmaGenerica = "Equipo Comercial — Botero Soto";

    /// <summary>#262: título del encabezado del correo (con el folio debajo, si se conoce).</summary>
    public const string TituloCorreo = "Cotización";

    /// <summary>#262: texto del encabezado en lugar del logo (sin <c>Correo:LogoUrl</c> válida) y texto alterno del logo.</summary>
    public const string Marca = "Botero Soto";

    /// <summary>#262: ancho del logo en el encabezado, en píxeles.</summary>
    public const int AnchoLogo = 90;

    /// <summary>Título del bloque del resumen (encima de la ficha de la cotización).</summary>
    public const string TituloResumen = "Resumen de la cotización";

    /// <summary>#262: título de la tabla de rutas.</summary>
    public const string TituloRutas = "Rutas cotizadas";

    /// <summary>#262: etiqueta de la ficha con cuántas rutas tienen precio (las que la Mesa no rechazó).</summary>
    public const string EtiquetaRutasCosteadas = "Rutas costeadas";

    /// <summary>Etiqueta del total del bloque del resumen (la misma del PDF).</summary>
    public const string EtiquetaTotal = clsTextosEstandar.EtiquetaTotalGeneral;

    /// <summary>
    /// #262 (decisión de producto del 2026-11-03): rutas que lista el resumen. Una licitación de cientos de rutas haría el correo muy
    /// largo y Gmail recorta los mensajes de más de ~102 KB (escondería el total y la firma): pasado el tope, el resto se resume en una
    /// línea y el detalle queda en el PDF adjunto. El total siempre es el de TODAS las rutas.
    /// </summary>
    public const int MaximoRutasResumen = 10;

    /// <summary>#262: línea que remite al PDF adjunto.</summary>
    public const string TextoAdjunto =
        "En el PDF adjunto está el detalle de cada ruta, lo que incluye y no incluye la tarifa, la facturación y la vigencia.";

    /// <summary>#262: ancho máximo del correo (en el celular ocupa el ancho de la pantalla).</summary>
    public const int AnchoMaximo = 840;

    // #262: diseño de cotización formal (lo escribe un comercial a su cliente): tarjeta blanca con la marca, el texto del comercial, la ficha
    // de la cotización, la tabla de rutas con el total resaltado, las notas, la despedida con la firma y el pie legal. Sin iconos ni botones.
    // Solo estilos en línea y tablas (Gmail, Outlook y el celular); sin imágenes ni <style>.
    private const string Azul = "#005AA8";
    private const string Texto = "#1A2A3A";
    private const string TextoSuave = "#5F6B7A";
    private const string Linea = "#D9DEE5";
    private const string FondoPagina = "#F0F2F5";
    private const string FondoSuave = "#F6F8FA";
    private const string FondoTotal = "#EAF2FB";

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
    /// #262: datos de la ficha del correo: folio, cliente y fecha de la cotización en la zona de negocio. Una cotización sin
    /// <c>CreatedAt</c> (todavía sin guardar) sale sin fecha.
    /// </summary>
    public static clsDatosCorreo Datos(clsCotizacionSolicitud cotizacion, IClsCalendarioNegocio calendario)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(calendario);
        return new clsDatosCorreo(
            cotizacion.Folio, cotizacion.ClienteNombre, cotizacion.CreatedAt == default ? null : calendario.FechaDe(cotizacion.CreatedAt));
    }

    /// <summary>
    /// Cuerpo en HTML (#262, cotización formal): tarjeta de hasta <see cref="AnchoMaximo"/> px con el encabezado (<see cref="TituloCorreo"/> con
    /// el folio a la izquierda y el logo de <paramref name="logoUrl"/> a la derecha), el aviso de pruebas (si el correo se redirige), el texto escapado (un <c>&lt;script&gt;</c> del comercial llega como texto) con los saltos de línea como <c>&lt;br&gt;</c>,
    /// el resumen (la ficha con el folio, la fecha y el cliente si se conocen, y las rutas costeadas), la tabla de rutas con el total (si hay),
    /// la nota del PDF adjunto y la despedida con la firma del comercial; debajo de la tarjeta, el pie legal.
    /// </summary>
    public static string Html(
        string texto,
        clsResumenCorreo? resumen = null,
        string? avisoPruebas = null,
        string? firmaComercial = null,
        clsDatosCorreo? datos = null,
        string? logoUrl = null,
        string? textoSeguros = null)
    {
        ArgumentNullException.ThrowIfNull(texto);
        return $"<div style=\"font-family:Arial,Helvetica,sans-serif;color:{Texto};font-size:14px;line-height:1.5;background-color:{FondoPagina};padding:24px 12px\">"
               // Outlook de escritorio ignora max-width: este envoltorio, que solo él lee, fija el ancho.
               + $"<!--[if mso]><table role=\"presentation\" width=\"{AnchoMaximo}\" align=\"center\" cellpadding=\"0\" cellspacing=\"0\"><tr><td><![endif]-->"
               + $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:{AnchoMaximo}px;margin:0 auto;"
               + $"background-color:#ffffff;border:1px solid {Linea};border-radius:12px;border-collapse:separate\">"
               + $"<tr><td style=\"padding:18px 24px;border-bottom:3px solid {Azul}\">{EncabezadoHtml(datos, logoUrl)}</td></tr>"
               + (avisoPruebas is null ? string.Empty : Fila(AvisoHtml(avisoPruebas)))
               + Fila(Escapar(texto))
               + (FichaHtml(datos, resumen) is { Length: > 0 } ficha ? Fila(TituloSeccion(TituloResumen) + ficha) : string.Empty)
               + (resumen is null ? string.Empty : Fila(ResumenHtml(resumen)))
               + Fila(NotaHtml(TextoAdjunto))
               // #261 (HU-046): el texto de seguros va aquí, después del resumen y antes de la despedida.
               + (string.IsNullOrWhiteSpace(textoSeguros) ? string.Empty : Fila(SegurosHtml(textoSeguros)))
               + Fila(FirmaHtml(firmaComercial), "20px 24px 24px 24px")
               + "</table>"
               + $"<div style=\"max-width:{AnchoMaximo}px;margin:0 auto;padding:12px 0 0 0;text-align:center;font-size:11px;color:{TextoSuave}\">"
               + $"{WebUtility.HtmlEncode(clsTextosEstandar.PieEmpresa)} &#183; "
               + $"<a href=\"{clsTextosEstandar.EnlaceWeb}\" style=\"color:{TextoSuave}\">{clsTextosEstandar.Web}</a></div>"
               + "<!--[if mso]></td></tr></table><![endif]-->"
               + "</div>";
    }

    /// <summary>Línea de las rutas que no se listan (pasado <see cref="MaximoRutasResumen"/>).</summary>
    public static string RutasRestantes(int rutas) =>
        string.Create(CultureInfo.InvariantCulture, $"… y {rutas} {(rutas == 1 ? "ruta más" : "rutas más")}: el detalle completo está en el PDF adjunto.");

    private static string Fila(string contenido, string relleno = "20px 24px 0 24px") =>
        $"<tr><td style=\"padding:{relleno}\">{contenido}</td></tr>";

    /// <summary>
    /// Encabezado: <see cref="TituloCorreo"/> con el folio debajo (si se conoce) a la izquierda y el logo a la derecha. Sin una URL http(s)
    /// absoluta, el logo se reemplaza por el texto <see cref="Marca"/>; con imágenes bloqueadas, ese mismo texto es el alterno del logo.
    /// </summary>
    private static string EncabezadoHtml(clsDatosCorreo? datos, string? logoUrl)
    {
        var folio = clsTextoSeguro.LimpiarLinea(datos?.Folio?.Trim()) is { Length: > 0 } f
            ? $"<div style=\"color:{TextoSuave};margin-top:2px\">{WebUtility.HtmlEncode(f)}</div>"
            : string.Empty;
        const string EstiloMarca = "color:" + Azul + ";font-size:14px;font-weight:bold";
        var logo = Uri.TryCreate(logoUrl?.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? $"<img src=\"{WebUtility.HtmlEncode(url.AbsoluteUri)}\" width=\"{AnchoLogo}\" alt=\"{Marca}\" "
              + $"style=\"display:inline-block;width:{AnchoLogo}px;max-width:{AnchoLogo}px;height:auto;border:0;{EstiloMarca}\">"
            : $"<span style=\"{EstiloMarca};white-space:nowrap\">{Marca}</span>";
        return "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\"><tr>"
               + $"<td style=\"vertical-align:middle\"><div style=\"font-size:18px;font-weight:bold;color:{Azul}\">{WebUtility.HtmlEncode(TituloCorreo)}</div>"
               + folio + "</td>"
               + $"<td style=\"vertical-align:middle;text-align:right;width:{AnchoLogo}px\">{logo}</td>"
               + "</tr></table>";
    }

    private static string TituloSeccion(string titulo) =>
        $"<div style=\"font-size:14px;font-weight:bold;letter-spacing:.06em;text-transform:uppercase;color:{Azul};margin-bottom:8px\">"
        + $"{WebUtility.HtmlEncode(titulo)}</div>";

    /// <summary>
    /// Ficha de la cotización en un recuadro gris claro, un dato debajo del otro: <c>Folio</c>, <c>Fecha</c> (3 de noviembre de 2026),
    /// <c>Cliente</c> y <c>Rutas costeadas</c> (cuántas tienen precio), con lo que se conozca.
    /// </summary>
    private static string FichaHtml(clsDatosCorreo? datos, clsResumenCorreo? resumen)
    {
        var filas = new List<(string Etiqueta, string Valor)>();
        if (clsTextoSeguro.LimpiarLinea(datos?.Folio?.Trim()) is { Length: > 0 } folio)
        {
            filas.Add(("Folio", folio));
        }

        if (datos?.Fecha is { } fecha)
        {
            filas.Add(("Fecha", string.Create(CultureInfo.InvariantCulture, $"{fecha.Day} de {clsFormatoFecha.Mes(fecha.Month)} de {fecha.Year}")));
        }

        if (clsTextoSeguro.LimpiarLinea(datos?.Cliente?.Trim()) is { Length: > 0 } cliente)
        {
            filas.Add(("Cliente", cliente));
        }

        if (resumen is not null)
        {
            filas.Add((EtiquetaRutasCosteadas, RutasCosteadas(resumen)));
        }

        return filas.Count == 0
            ? string.Empty
            : $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:{FondoSuave};"
              + $"border:1px solid {Linea};border-radius:8px;border-collapse:separate;font-size:14px\">"
              + "<tr><td style=\"padding:10px 14px\"><table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse\">"
              + string.Concat(filas.Select(f =>
                  $"<tr><td style=\"padding:3px 16px 3px 0;color:{TextoSuave};white-space:nowrap;vertical-align:top\">{f.Etiqueta}</td>"
                  + $"<td style=\"padding:3px 0;font-weight:bold\">{WebUtility.HtmlEncode(f.Valor)}</td></tr>"))
              + "</table></td></tr></table>";
    }

    /// <summary>
    /// "Cordialmente," y la firma del comercial, una parte de <see cref="FirmaComercial"/> por línea (nombre, correo y celular) en negrita,
    /// con la misma letra del texto. Sin firma, <see cref="FirmaGenerica"/>.
    /// </summary>
    private static string FirmaHtml(string? firmaComercial)
    {
        var partes = (string.IsNullOrWhiteSpace(firmaComercial) ? FirmaGenerica : firmaComercial.Trim()).Split(" · ");
        return $"<div style=\"margin-bottom:14px\">{WebUtility.HtmlEncode(clsTextosEstandar.Despedida)}</div>"
               + string.Concat(partes.Select(p => $"<div style=\"font-weight:bold\">{WebUtility.HtmlEncode(p)}</div>"));
    }

    /// <summary>Cantidad de rutas con precio (las que la Mesa no rechazó).</summary>
    public static string RutasCosteadas(clsResumenCorreo resumen)
    {
        ArgumentNullException.ThrowIfNull(resumen);
        return string.Create(CultureInfo.InvariantCulture, $"{resumen.Rutas.Count(r => r.Precio is not null)}");
    }

    /// <summary>Nota en un recuadro azul claro con un ícono informativo ("!" en un círculo azul, sin imágenes).</summary>
    private static string NotaHtml(string nota) =>
        $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:{FondoTotal};border-radius:8px;"
        + "border-collapse:separate\"><tr>"
        + "<td style=\"padding:10px 0 10px 14px;width:18px;vertical-align:top\">"
        + $"<div style=\"width:18px;height:18px;line-height:18px;border-radius:9px;background-color:{Azul};color:#ffffff;font-size:12px;"
        + "font-weight:bold;text-align:center\">!</div></td>"
        + $"<td style=\"padding:10px 14px 10px 10px;font-size:14px\"><b>Nota:</b> {WebUtility.HtmlEncode(nota)}</td></tr></table>";

    /// <summary>
    /// HU-033 (auditoría #199): firma del comercial en el cuerpo ("Nombre · correo · Cel. teléfono"): con el <c>From</c> corporativo el
    /// cliente sigue sabiendo a quién responder (MailSender no tiene <c>Reply-To</c>). <c>null</c> sin datos.
    /// </summary>
    public static string? FirmaComercial(string? nombre, string? correo, string? telefono)
    {
        var partes = new[] { nombre, correo, string.IsNullOrWhiteSpace(telefono) ? null : $"Cel. {telefono.Trim()}" }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => clsTextoSeguro.LimpiarLinea(p!.Trim()))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        return partes.Count == 0 ? null : string.Join(" · ", partes);
    }

    /// <summary>
    /// #261 (HU-046): bloque del texto de seguros de la propuesta, después del resumen y antes de la despedida: recuadro gris claro con la
    /// etiqueta <see cref="clsTextosEstandar.TituloSegurosPropuesta"/> en negrita y el texto escapado (los saltos de línea como <c>&lt;br&gt;</c>).
    /// </summary>
    private static string SegurosHtml(string textoSeguros) =>
        $"<div style=\"background-color:{FondoSuave};border:1px solid {Linea};border-radius:8px;padding:12px 14px;font-size:13px;color:{TextoSuave}\">"
        + $"<b style=\"color:{Texto}\">{WebUtility.HtmlEncode(clsTextosEstandar.TituloSegurosPropuesta)}</b><br>"
        + Escapar(textoSeguros)
        + "</div>";

    /// <summary>Aviso de pruebas (correo redirigido), con franja naranja a la izquierda, antes del texto del comercial.</summary>
    public static string AvisoHtml(string aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return "<div style=\"background-color:#FFF8EC;border-left:4px solid #F0A040;padding:10px 14px;font-size:12px;color:#6B4A12\">"
               + Escapar(aviso)
               + "</div>";
    }

    /// <summary>
    /// Bloque fijo del resumen: tabla con una fila por ruta y su precio (hasta <see cref="MaximoRutasResumen"/>; el resto en una fila que
    /// remite al PDF) y, al pie, el total de TODAS las rutas resaltado; en USD, debajo, la nota de la TRM. #262: la leyenda de impuestos ya no
    /// va en el correo (decisión de producto del 2026-11-03); sigue en el PDF adjunto.
    /// </summary>
    public static string ResumenHtml(clsResumenCorreo resumen)
    {
        ArgumentNullException.ThrowIfNull(resumen);
        const string Celda = "padding:9px 10px;border-bottom:1px solid " + Linea;
        const string Encabezado = "padding:8px 10px;background-color:" + FondoSuave + ";border-bottom:1px solid " + Linea
                                  + ";font-size:14px;font-weight:bold;letter-spacing:.04em;text-transform:uppercase;color:" + TextoSuave;
        var filas = string.Concat(resumen.Rutas.Take(MaximoRutasResumen).Select(r =>
            $"<tr><td style=\"{Celda};color:{TextoSuave};width:24px\">{r.Orden.ToString(CultureInfo.InvariantCulture)}</td>"
            + $"<td style=\"{Celda}\">{WebUtility.HtmlEncode(r.Ruta)}</td>"
            + $"<td style=\"{Celda};text-align:right;white-space:nowrap{(r.Precio is null ? $";color:{TextoSuave}" : string.Empty)}\">"
            + $"{WebUtility.HtmlEncode(r.PrecioTexto)}</td></tr>"));
        var restantes = resumen.Rutas.Count - MaximoRutasResumen;
        return TituloSeccion(TituloRutas)
               + $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid {Linea};border-radius:8px;"
               + "border-collapse:separate;border-spacing:0;font-size:14px\">"
               + $"<tr><td style=\"{Encabezado}\">#</td><td style=\"{Encabezado}\">Ruta</td><td style=\"{Encabezado};text-align:right\">Valor</td></tr>"
               + filas
               + (restantes > 0
                   ? $"<tr><td colspan=\"3\" style=\"{Celda};font-size:14px;color:{TextoSuave}\">{WebUtility.HtmlEncode(RutasRestantes(restantes))}</td></tr>"
                   : string.Empty)
               // El total al pie de la tabla: la etiqueta se parte en dos líneas en un celular angosto, el valor nunca.
               + $"<tr><td colspan=\"2\" style=\"padding:12px 10px;background-color:{FondoTotal};text-align:right;font-size:14px;font-weight:bold\">"
               + $"{WebUtility.HtmlEncode(EtiquetaTotal)}</td>"
               + $"<td style=\"padding:12px 10px;background-color:{FondoTotal};text-align:right;white-space:nowrap;font-size:14px;font-weight:bold;color:{Azul}\">"
               + WebUtility.HtmlEncode($"{resumen.TotalTexto} {resumen.Moneda}") + "</td></tr>"
               + "</table>"
               + (resumen.NotaMoneda is { } nota ? $"<div style=\"font-size:14px;color:{TextoSuave};margin-top:4px\">{WebUtility.HtmlEncode(nota)}</div>" : string.Empty);
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

/// <summary>
/// #262: datos opcionales de la cotización para la ficha del correo (folio, fecha y cliente). Todos pueden ser <c>null</c>.
/// </summary>
/// <param name="Folio">Folio de la cotización.</param>
/// <param name="Cliente">Nombre del cliente.</param>
/// <param name="Fecha">Fecha de la cotización en la zona de negocio (<c>IClsCalendarioNegocio.FechaDe</c>).</param>
public sealed record clsDatosCorreo(string? Folio = null, string? Cliente = null, DateOnly? Fecha = null);
