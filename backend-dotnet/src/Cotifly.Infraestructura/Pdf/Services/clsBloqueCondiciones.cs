using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using MigraDoc.DocumentObjectModel;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Condiciones: facturación y forma de pago, seguro, días libres (si vienen), horas logísticas adicionales / stand by y
/// vigencia. En tarifa única ocupan la tercera página con títulos <c>h2</c>; en multi van compactas en la primera página
/// (<c>.cond</c>: títulos de 10 px y párrafos de 9 px).
/// </summary>
internal static class clsBloqueCondiciones
{
    public static void Agregar(Section seccion, clsDocumentoCotizacion documento, clsDisenoPdf diseno)
    {
        var compacto = documento.Variante != VarianteDocumento.TarifaUnica;
        Condicion(seccion, clsTextosEstandar.TituloFacturacion, [documento.Facturacion], diseno, compacto);
        Condicion(seccion, clsTextosEstandar.TituloSeguro, documento.Seguro, diseno, compacto);
        if (documento.DiasLibres is { } diasLibres)
        {
            Condicion(seccion, clsTextosEstandar.TituloDiasLibres, [diasLibres], diseno, compacto);
        }

        Condicion(seccion, documento.TituloStandBy, [documento.StandBy], diseno, compacto);
        Condicion(seccion, clsTextosEstandar.TituloVigencia, [documento.Vigencia], diseno, compacto);
    }

    private static void Condicion(Section seccion, string titulo, IReadOnlyList<string> parrafos, clsDisenoPdf diseno, bool compacto)
    {
        if (compacto)
        {
            clsBloqueTarifa.Titulo(seccion, titulo, 10, (5, 2), diseno);
        }
        else
        {
            clsBloqueTarifa.Titulo(seccion, titulo, diseno.H2, diseno.MargenH2, diseno);
        }

        foreach (var texto in parrafos)
        {
            var parrafo = compacto
                ? Parrafo(seccion.AddParagraph(), 9, diseno.Alto, alineacion: ParagraphAlignment.Justify, despues: 3)
                : Parrafo(seccion.AddParagraph(), diseno.Parrafo, diseno.Alto, alineacion: ParagraphAlignment.Justify, despues: diseno.EspacioParrafo);
            AgregarTexto(parrafo, texto);
        }
    }
}
