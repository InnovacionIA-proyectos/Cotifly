using Cotifly.Aplicacion.Pdf.Services;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Encabezado y pie fijos de cada página (<c>.logo</c> y <c>.footer</c> con <c>position:fixed</c> del CSS): logo arriba a la
/// derecha; abajo las direcciones (<c>FOOTER_DIRS</c>) centradas y, en tarifa única, la franja azul con la web a la derecha.
/// </summary>
internal static class clsEncabezadoPiePdf
{
    public static void Agregar(Section seccion, clsDisenoPdf diseno)
    {
        var encabezado = seccion.Headers.Primary.AddParagraph();
        encabezado.Format.Alignment = ParagraphAlignment.Right;
        encabezado.Format.LineSpacingRule = LineSpacingRule.Single;
        var logo = encabezado.AddImage(clsRecursosPdf.ComoFuenteMigraDoc(clsRecursosPdf.ImagenLogo()));
        logo.Width = Mm(diseno.AnchoLogoMm);
        logo.LockAspectRatio = true;

        var pie = seccion.Footers.Primary.AddTable();
        // Tres columnas: el texto centrado con 38 mm de aire a cada lado (padding 0 38mm) y la franja web en el de la derecha.
        TablaSinBordes(pie, diseno.AnchoUtilMm, 38, diseno.AnchoUtilMm - 76, 38);
        pie.TopPadding = 0;
        pie.BottomPadding = 0;
        var fila = pie.AddRow();
        fila.VerticalAlignment = VerticalAlignment.Top;

        var direcciones = Parrafo(fila.Cells[1].AddParagraph(), 7, 1.5, Atenuado, alineacion: ParagraphAlignment.Center, despues: 4);
        direcciones.AddText(clsTextosEstandar.PieEmpresa);
        direcciones.AddLineBreak();
        for (var i = 0; i < clsTextosEstandar.SedesPie.Count; i++)
        {
            var (sede, direccion) = clsTextosEstandar.SedesPie[i];
            if (i > 0)
            {
                direcciones.AddText(" · ");
            }

            direcciones.AddFormattedText(sede, TextFormat.Bold);
            direcciones.AddText(" " + direccion);
        }

        if (diseno.FranjaWeb)
        {
            var celda = fila.Cells[2];
            celda.Shading.Color = Primario;
            celda.VerticalAlignment = VerticalAlignment.Center;
            var web = Parrafo(celda.AddParagraph(), 9, 1.5, Blanco, negrita: true, ParagraphAlignment.Right, antes: 5, despues: 5);
            web.Format.RightIndent = Px(14);
            web.AddText(clsTextosEstandar.Web);
        }
    }
}
