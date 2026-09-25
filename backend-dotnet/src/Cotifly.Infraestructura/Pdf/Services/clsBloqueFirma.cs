using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using MigraDoc.DocumentObjectModel;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Cierre: frase final centrada con el enlace a la web (<c>.frase-final</c> en tarifa única, <c>.web-cierre</c> con línea
/// superior en multi) y firma del comercial (<c>.firma</c>). "Cel" solo se imprime si <c>UsuariosWeb.telefono</c> tiene valor.
/// </summary>
internal static class clsBloqueFirma
{
    /// <summary>Frase final de tarifa única (antes de la firma): 16 px, negrita, azul, centrada.</summary>
    public static void AgregarFraseFinal(Section seccion, clsFraseFinalDocumento frase)
    {
        var parrafo = Parrafo(seccion.AddParagraph(), 16, 1.4, Primario, negrita: true, ParagraphAlignment.Center, antes: 16);
        AgregarTexto(parrafo, frase.Texto);
        if (frase.Enlace is { } enlace)
        {
            parrafo.AddLineBreak();
            Enlace(parrafo, enlace, Primario, subrayado: true); // el <a> del CSS de tarifa única se subraya
        }
    }

    /// <summary>Cierre de la variante multi (después de la firma): línea superior, 16 pt, enlace en azul <c>#0057B8</c>.</summary>
    public static void AgregarCierreWeb(Section seccion, clsFraseFinalDocumento frase)
    {
        var parrafo = seccion.AddParagraph();
        parrafo.Format.Font.Size = 16;
        parrafo.Format.LineSpacingRule = LineSpacingRule.Exactly;
        parrafo.Format.LineSpacing = Unit.FromPoint(16 * 1.45);
        parrafo.Format.Font.Color = Texto;
        parrafo.Format.Alignment = ParagraphAlignment.Center;
        parrafo.Format.SpaceBefore = Px(18);
        parrafo.Format.Borders.Top.Visible = true;
        parrafo.Format.Borders.Top.Width = 0.75;
        parrafo.Format.Borders.Top.Color = Borde;
        parrafo.Format.Borders.DistanceFromTop = Px(8);
        AgregarTexto(parrafo, frase.Texto + " ");
        if (frase.Enlace is { } enlace)
        {
            Enlace(parrafo, enlace, AzulMulti, subrayado: false); // text-decoration: none en armar_multi.py
        }
    }

    public static void AgregarFirma(Section seccion, clsFirmaDocumento firma, clsDisenoPdf diseno)
    {
        var despedida = Parrafo(seccion.AddParagraph(), diseno.Parrafo, diseno.Alto, antes: diseno.MargenFirma, despues: diseno.EspacioParrafo);
        despedida.Format.KeepWithNext = true;
        despedida.AddText(clsTextosEstandar.Despedida);

        var nombre = Parrafo(seccion.AddParagraph(), diseno.Cuerpo, diseno.Alto, negrita: true, antes: diseno.MargenNombreFirma);
        nombre.Format.KeepWithNext = true;
        AgregarTexto(nombre, firma.Nombre);

        string?[] detalles =
        [
            firma.Cargo,
            firma.Email,
            firma.Celular is null ? null : $"{clsTextosEstandar.EtiquetaCelular} {firma.Celular}",
        ];
        var presentes = detalles.OfType<string>().ToList();
        for (var i = 0; i < presentes.Count; i++)
        {
            var detalle = Parrafo(seccion.AddParagraph(), diseno.DetalleFirma, diseno.Alto, Atenuado);
            detalle.Format.KeepWithNext = i < presentes.Count - 1;
            AgregarTexto(detalle, presentes[i]);
        }
    }

    private static void Enlace(Paragraph parrafo, string url, Color color, bool subrayado)
    {
        var enlace = parrafo.AddHyperlink(url, HyperlinkType.Url);
        var texto = enlace.AddFormattedText(clsTextosEstandar.Web);
        texto.Color = color;
        texto.Bold = true;
        if (subrayado)
        {
            texto.Underline = Underline.Single;
        }
    }
}
