using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Portada / carta (primera página): fecha larga, referencia (folio) a la derecha, cliente, contactos, asunto, introducción
/// y, en tarifa única, la imagen alusiva de la línea con su leyenda en franja verde (<c>.dibujo-import</c>).
/// </summary>
internal static class clsBloquePortada
{
    /// <summary>Ancho máximo de la imagen de la línea (<c>max-width: 460px</c>).</summary>
    private const double AnchoImagenPx = 460;

    public static void Agregar(Section seccion, clsDocumentoCotizacion documento, clsDisenoPdf diseno)
    {
        var alto = diseno.Alto;
        Parrafo(seccion.AddParagraph(documento.FechaLarga), diseno.Cuerpo, alto);

        var referencia = Parrafo(seccion.AddParagraph(), 12, alto, negrita: true, alineacion: ParagraphAlignment.Right);
        referencia.Format.SpaceBefore = Mm(11);
        AgregarTexto(referencia, documento.Referencia);

        var cliente = Parrafo(seccion.AddParagraph(), diseno.Cliente, alto, negrita: true, antes: diseno.MargenCliente);
        AgregarTexto(cliente, documento.Cliente);

        foreach (var contacto in documento.Contactos)
        {
            AgregarTexto(Parrafo(seccion.AddParagraph(), diseno.Cuerpo, alto), contacto);
        }

        var asunto = Parrafo(seccion.AddParagraph(), diseno.Cuerpo, alto, antes: diseno.MargenAsunto, despues: diseno.MargenAsunto);
        asunto.AddFormattedText(clsTextosEstandar.EtiquetaAsunto, TextFormat.Bold);
        asunto.AddText(" ");
        AgregarTexto(asunto, documento.Asunto);

        foreach (var parrafo in documento.Introduccion)
        {
            if (parrafo.EsVineta)
            {
                Vinetado(seccion.AddParagraph(), parrafo.Texto, diseno.Parrafo, alto, 16, 3);
            }
            else
            {
                var p = Parrafo(seccion.AddParagraph(), diseno.Parrafo, alto, alineacion: ParagraphAlignment.Justify, despues: diseno.EspacioParrafo);
                AgregarTexto(p, parrafo.Texto);
            }
        }

        if (documento.ImagenLinea is { } imagen)
        {
            AgregarImagenLinea(seccion, imagen, documento.LeyendaImagen, diseno);
        }
    }

    private static void AgregarImagenLinea(Section seccion, string clave, string? leyenda, clsDisenoPdf diseno)
    {
        var ancho = Math.Min(Px(AnchoImagenPx).Millimeter, diseno.AnchoUtilMm);
        Espacio(seccion, 10);

        var tabla = seccion.AddTable();
        tabla.Rows.Alignment = RowAlignment.Center;
        tabla.AddColumn(Mm(ancho));
        tabla.Borders.Visible = false;
        Relleno(tabla, 0, 0);

        var filaImagen = tabla.AddRow();
        var celdaImagen = filaImagen.Cells[0];
        celdaImagen.Borders.Width = 0.75;
        celdaImagen.Borders.Color = Borde;
        var contenedor = celdaImagen.AddParagraph();
        contenedor.Format.Alignment = ParagraphAlignment.Center;
        contenedor.Format.LineSpacingRule = LineSpacingRule.Single;
        var foto = contenedor.AddImage(clsRecursosPdf.ComoFuenteMigraDoc(clsRecursosPdf.ImagenLinea(clave)));
        foto.Width = Mm(ancho - 0.5);
        foto.LockAspectRatio = true;

        if (leyenda is not null)
        {
            var filaLeyenda = tabla.AddRow();
            filaLeyenda.KeepWith = 0;
            filaImagen.KeepWith = 1;
            var celda = filaLeyenda.Cells[0];
            celda.Shading.Color = Secundario;
            var texto = Parrafo(celda.AddParagraph(), 11, diseno.Alto, Blanco, negrita: true, ParagraphAlignment.Center, antes: 7, despues: 7);
            texto.Format.LeftIndent = Px(10);
            texto.Format.RightIndent = Px(10);
            AgregarTexto(texto, leyenda);
        }

        Espacio(seccion, 14);
    }
}
