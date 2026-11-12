namespace Cotifly.Aplicacion.Pdf.DTOs;

/// <summary>
/// Salida de <c>generar_pdf</c> (esquema <c>SalidaPdf</c> de <c>specs/openapi.yaml</c>:
/// <c>{cotizacion_id, folio, pdf_url, registrada_en_bd}</c>).
/// </summary>
/// <param name="PdfUrl"><c>{Pdf:BaseUrl}/pdfs/{folio}.pdf</c>; sin <c>Pdf:BaseUrl</c> es relativa (<c>/pdfs/...</c>), servida por <c>GET /pdfs/{archivo}</c>.</param>
public sealed record clsSalidaPdf(Guid CotizacionId, string Folio, string PdfUrl, bool RegistradaEnBd)
{
    /// <summary>
    /// HU-045.3 (#265): en el PDF del costeo, las rutas con el seguro de la mercancía marcado sin valor de la mercancía (el PDF salió sin
    /// su fila); <c>null</c> si no hay ninguna o en el PDF del chat.
    /// </summary>
    public Costeo.DTOs.clsAvisoSeguro? AvisoSeguro { get; init; }
}

/// <summary>PDF renderizado por <c>IClsGeneradorPdf</c>.</summary>
/// <param name="Contenido">Bytes del PDF.</param>
/// <param name="Paginas">Número de páginas.</param>
public sealed record clsPdfGenerado(byte[] Contenido, int Paginas);
