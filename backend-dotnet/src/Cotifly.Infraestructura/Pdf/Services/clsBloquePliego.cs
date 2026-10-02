using Cotifly.Aplicacion.Pdf.DTOs;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Resumen del pliego (HU-011.5, ADR-017 §6): tabla con una fila por ruta (número, ruta, vehículo y peso, tarifa) con los mismos
/// colores de <see cref="clsBloqueTarifa"/> (franja verde con el título, encabezado gris, tarifa en azul), el total del pliego en
/// la franja azul y la nota de referencia. El encabezado de la tabla se repite en cada página: un pliego de muchas rutas sigue legible.
/// </summary>
internal static class clsBloquePliego
{
    private static readonly double[] _proporciones = [8, 44, 28, 20];
    private static readonly string[] _columnas = ["N.º", "Ruta", "Vehículo y peso", "Tarifa"];

    public static void Agregar(Section seccion, clsResumenPliegoDocumento resumen, clsDisenoPdf diseno)
    {
        clsBloqueTarifa.Titulo(seccion, resumen.Titulo, diseno.H1, diseno.MargenH1, diseno);
        Espacio(seccion, diseno.MargenTabla);

        var tabla = seccion.AddTable();
        tabla.Borders.Width = 0.75;
        tabla.Borders.Color = Borde;
        var total = _proporciones.Sum();
        foreach (var proporcion in _proporciones)
        {
            tabla.AddColumn(Mm(diseno.AnchoUtilMm * proporcion / total));
        }

        Relleno(tabla, diseno.RellenoCelda.Vertical, diseno.RellenoCelda.Horizontal);

        var encabezado = tabla.AddRow();
        encabezado.HeadingFormat = true;
        encabezado.Shading.Color = Secundario;
        encabezado.TopPadding = Px(diseno.RellenoEncabezado);
        encabezado.BottomPadding = Px(diseno.RellenoEncabezado);
        for (var i = 0; i < _columnas.Length; i++)
        {
            AgregarTexto(Parrafo(encabezado.Cells[i].AddParagraph(), diseno.EncabezadoTabla, diseno.Alto, Blanco, negrita: true, ParagraphAlignment.Center),
                _columnas[i].ToUpperInvariant());
        }

        var par = false;
        foreach (var fila in resumen.Filas)
        {
            var renglon = tabla.AddRow();
            renglon.VerticalAlignment = VerticalAlignment.Center;
            if (par)
            {
                renglon.Shading.Color = Fondo;
            }

            par = !par;
            AgregarTexto(Parrafo(renglon.Cells[0].AddParagraph(), diseno.CeldaTabla, diseno.Alto, negrita: true, alineacion: ParagraphAlignment.Center), fila.Numero);
            AgregarTexto(Parrafo(renglon.Cells[1].AddParagraph(), diseno.CeldaTabla, diseno.Alto), fila.Ruta);
            AgregarTexto(Parrafo(renglon.Cells[2].AddParagraph(), diseno.CeldaTabla, diseno.Alto), fila.VehiculoPeso);
            AgregarTexto(Parrafo(renglon.Cells[3].AddParagraph(), diseno.CeldaTabla, diseno.Alto, Primario, negrita: true, ParagraphAlignment.Right), fila.Tarifa);
        }

        var totalFila = tabla.AddRow();
        totalFila.Shading.Color = Primario;
        totalFila.TopPadding = Px(diseno.RellenoOrigen);
        totalFila.BottomPadding = Px(diseno.RellenoOrigen);
        totalFila.KeepWith = 0;
        var etiqueta = totalFila.Cells[0];
        etiqueta.MergeRight = 2;
        AgregarTexto(Parrafo(etiqueta.AddParagraph(), diseno.OrigenTabla, diseno.Alto, Blanco, negrita: true, ParagraphAlignment.Right), resumen.EtiquetaTotal);
        AgregarTexto(Parrafo(totalFila.Cells[3].AddParagraph(), diseno.OrigenTabla, diseno.Alto, Blanco, negrita: true, ParagraphAlignment.Right), resumen.Total);

        var nota = Parrafo(seccion.AddParagraph(), diseno.NotaGeneral, diseno.Alto, Atenuado, alineacion: ParagraphAlignment.Justify, antes: diseno.MargenNotaGeneral);
        AgregarTexto(nota, resumen.Nota);
    }
}
