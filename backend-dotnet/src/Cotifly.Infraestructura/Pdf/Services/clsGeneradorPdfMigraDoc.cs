using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Adaptador de <see cref="IClsGeneradorPdf"/> con PDFsharp-MigraDoc 6 (MIT, ADR-006). Reproduce el layout de
/// <c>armar_cotizacion.py</c> (tarifa única, 3 páginas: carta; tarifa; condiciones y firma) y de <c>armar_multi.py</c>
/// (multi-sección: carta con condiciones; tarifas y firma), con un componente por bloque (<see cref="clsBloquePortada"/>,
/// <see cref="clsBloqueTarifa"/>, <see cref="clsBloqueCondiciones"/>, <see cref="clsBloqueFirma"/>) y el encabezado y pie
/// en <see cref="clsEncabezadoPiePdf"/>. Fuentes y recursos embebidos (<see cref="clsResolvedorFuentes"/>,
/// <see cref="clsRecursosPdf"/>): no depende de fuentes del sistema ni de archivos temporales.
/// </summary>
/// <remarks>
/// Diferencias inevitables con el HTML: MigraDoc no tiene esquinas redondeadas (franja web y leyenda de la imagen quedan
/// rectas) ni espaciado entre letras (encabezados de la tabla); el flex de dos columnas y la franja web flotante se
/// aproximan con tablas; los márgenes CSS que se colapsaban se suman como espacio antes/después. Fuente Liberation Sans
/// (métricas de Arial, la alternativa del CSS) en lugar de Stilu, sin licencia disponible.
/// </remarks>
public sealed class clsGeneradorPdfMigraDoc : IClsGeneradorPdf
{
    public clsGeneradorPdfMigraDoc() => clsResolvedorFuentes.Registrar();

    public clsPdfGenerado Generar(clsDocumentoCotizacion documento)
    {
        ArgumentNullException.ThrowIfNull(documento);
        var modelo = Construir(documento);

        var renderizador = new PdfDocumentRenderer { Document = modelo };
        renderizador.RenderDocument();
        renderizador.PdfDocument.Info.Title = $"Cotización {documento.Referencia}";
        renderizador.PdfDocument.Info.Author = "Botero Soto Soluciones Logísticas";
        renderizador.PdfDocument.Info.Creator = "Cotifly";

        var paginas = renderizador.PdfDocument.PageCount; // antes de guardar: después el documento queda cerrado
        using var memoria = new MemoryStream();
        renderizador.PdfDocument.Save(memoria, false);
        return new clsPdfGenerado(memoria.ToArray(), paginas);
    }

    /// <summary>Modelo MigraDoc del documento (separado del render para poder inspeccionarlo en pruebas).</summary>
    internal static Document Construir(clsDocumentoCotizacion documento)
    {
        var esMulti = documento.Variante != VarianteDocumento.TarifaUnica;
        var diseno = esMulti ? clsDisenoPdf.MultiSeccion : clsDisenoPdf.TarifaUnica;

        var modelo = new Document();
        modelo.Info.Title = $"Cotización {documento.Referencia}";
        var normal = modelo.Styles[StyleNames.Normal]!;
        normal.Font.Name = clsResolvedorFuentes.Familia;
        normal.Font.Color = Texto;
        Tamano(normal.ParagraphFormat, diseno.Cuerpo, diseno.Alto);

        var seccion = modelo.AddSection();
        ConfigurarPagina(seccion.PageSetup, diseno);
        clsEncabezadoPiePdf.Agregar(seccion, diseno);

        clsBloquePortada.Agregar(seccion, documento, diseno);
        if (esMulti)
        {
            // armar_multi.py: condiciones en la hoja 1; tarifas, listas, firma y cierre web en la hoja 2.
            clsBloqueCondiciones.Agregar(seccion, documento, diseno);
            seccion.AddPageBreak();

            // HU-011.5: el pliego abre con el resumen de todas sus rutas y el total; luego las tablas por origen.
            if (documento.Variante == VarianteDocumento.Pliego && documento.Pliego is { } pliego)
            {
                clsBloquePliego.Agregar(seccion, pliego, diseno);
            }

            clsBloqueTarifa.Agregar(seccion, documento, diseno);
            clsBloqueFirma.AgregarFirma(seccion, documento.Firma, diseno);
            clsBloqueFirma.AgregarCierreWeb(seccion, documento.FraseFinal);
        }
        else
        {
            // armar_cotizacion.py: carta; tarifa; condiciones, frase final y firma (dos saltos de página, .pb).
            seccion.AddPageBreak();
            clsBloqueTarifa.Agregar(seccion, documento, diseno);
            seccion.AddPageBreak();
            clsBloqueCondiciones.Agregar(seccion, documento, diseno);
            clsBloqueFirma.AgregarFraseFinal(seccion, documento.FraseFinal);
            clsBloqueFirma.AgregarFirma(seccion, documento.Firma, diseno);
        }

        return modelo;
    }

    private static void ConfigurarPagina(PageSetup pagina, clsDisenoPdf diseno)
    {
        pagina.PageFormat = PageFormat.A4;
        pagina.PageWidth = Mm(210);
        pagina.PageHeight = Mm(297);
        pagina.Orientation = Orientation.Portrait;
        pagina.TopMargin = Mm(diseno.MargenSuperiorMm);
        pagina.BottomMargin = Mm(diseno.MargenInferiorMm);
        pagina.LeftMargin = Mm(diseno.MargenLateralMm);
        pagina.RightMargin = Mm(diseno.MargenLateralMm);
        pagina.HeaderDistance = Mm(diseno.EncabezadoMm);
        pagina.FooterDistance = Mm(diseno.PieMm);
    }
}
