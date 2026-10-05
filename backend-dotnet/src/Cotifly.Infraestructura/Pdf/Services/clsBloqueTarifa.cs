using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using static Cotifly.Infraestructura.Pdf.Services.clsEstilosPdf;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Bloque "NUESTRA TARIFA PARA SU SOLICITUD ES:": subtítulo, tabla(s) de tarifa (<c>table.tar</c>: franja verde con el
/// título, franja azul con el origen, encabezados en mayúscula y filas con la tarifa en azul), nota general, nota de decretos
/// (<c>.nota-decreto</c>), aclaración de la cotización en dólares (HU-012.3, mismo recuadro), detalle adicional (aduana, extradimensionada, refrigerada) y las dos columnas incluye / no incluye.
/// </summary>
internal static class clsBloqueTarifa
{
    /// <summary>Reparto de columnas de la tabla (el HTML usaba ancho automático; la nota se lleva el resto).</summary>
    private static readonly double[] _proporciones = [15, 22, 15, 48];

    public static void Agregar(Section seccion, clsDocumentoCotizacion documento, clsDisenoPdf diseno)
    {
        var esMulti = documento.Variante != VarianteDocumento.TarifaUnica;
        Titulo(seccion, clsTextosEstandar.TituloTarifa, diseno.H1, diseno.MargenH1, diseno);
        if (documento.Subtitulo is { } subtitulo)
        {
            Titulo(seccion, subtitulo, diseno.H2, diseno.MargenH2, diseno);
        }

        var columnas = esMulti ? clsTextosEstandar.ColumnasMulti : clsTextosEstandar.ColumnasUnica;
        foreach (var tabla in documento.Secciones)
        {
            Espacio(seccion, diseno.MargenTabla);
            AgregarTabla(seccion, tabla, columnas, diseno);
            if (tabla.NotaGeneral is { } notaSeccion)
            {
                NotaGeneral(seccion, notaSeccion, diseno);
            }
        }

        if (documento.NotaGeneral is { } nota)
        {
            NotaGeneral(seccion, nota, diseno);
        }

        if (documento.NotaDecreto is { } decreto)
        {
            NotaResaltada(seccion, clsTextosEstandar.EtiquetaTiemposLogisticos, decreto, diseno);
        }

        // HU-012.3: en USD, la nota de TRM mínima con la TRM aplicada, en el mismo recuadro de la nota de decretos y SIN etiqueta: el PDF
        // muestra solo el texto exacto que definió negocio (ambas variantes y el pliego).
        if (documento.NotaMoneda is { } moneda)
        {
            NotaResaltada(seccion, null, moneda, diseno);
        }

        if (documento.BloquesAdicionales.Count > 0)
        {
            Subtitulo(seccion, clsTextosEstandar.TituloDetalleAdicional, diseno);
            foreach (var bloque in documento.BloquesAdicionales)
            {
                Subtitulo(seccion, bloque.Titulo, diseno);
                AgregarDatos(seccion, bloque, diseno);
            }
        }

        AgregarListas(seccion, documento, diseno);
    }

    /// <summary>Título de sección (<c>h1</c>/<c>h2</c>: azul primario, negrita), unido al bloque siguiente.</summary>
    public static void Titulo(Section seccion, string texto, double px, (double Antes, double Despues) margen, clsDisenoPdf diseno)
    {
        var titulo = Parrafo(seccion.AddParagraph(), px, diseno.Alto, Primario, negrita: true, antes: margen.Antes, despues: margen.Despues);
        titulo.Format.KeepWithNext = true;
        AgregarTexto(titulo, texto);
    }

    /// <summary>Subtítulo de bloque (<c>h3.cond-h</c>).</summary>
    public static void Subtitulo(Section seccion, string texto, clsDisenoPdf diseno)
    {
        var titulo = Parrafo(seccion.AddParagraph(), diseno.H3, diseno.Alto, diseno.ColorH3, negrita: true, antes: diseno.MargenH3.Antes, despues: diseno.MargenH3.Despues);
        titulo.Format.KeepWithNext = true;
        AgregarTexto(titulo, texto);
    }

    private static void AgregarTabla(Section seccion, clsSeccionDocumento datos, IReadOnlyList<string> columnas, clsDisenoPdf diseno)
    {
        var tabla = seccion.AddTable();
        tabla.Borders.Width = 0.75;
        tabla.Borders.Color = Borde;
        var total = _proporciones.Sum();
        foreach (var proporcion in _proporciones)
        {
            tabla.AddColumn(Mm(diseno.AnchoUtilMm * proporcion / total));
        }

        Relleno(tabla, diseno.RellenoCelda.Vertical, diseno.RellenoCelda.Horizontal);

        var titulo = tabla.AddRow();
        titulo.HeadingFormat = true;
        Franja(titulo, datos.Titulo, Secundario, diseno.TituloTabla, diseno.RellenoTitulo, diseno);

        var origen = tabla.AddRow();
        origen.HeadingFormat = true;
        Franja(origen, clsTextosEstandar.PrefijoOrigen + datos.Origen, Primario, diseno.OrigenTabla, diseno.RellenoOrigen, diseno);

        var encabezado = tabla.AddRow();
        encabezado.HeadingFormat = true;
        encabezado.Shading.Color = Fondo;
        encabezado.TopPadding = Px(diseno.RellenoEncabezado);
        encabezado.BottomPadding = Px(diseno.RellenoEncabezado);
        for (var i = 0; i < columnas.Count; i++)
        {
            var celda = Parrafo(encabezado.Cells[i].AddParagraph(), diseno.EncabezadoTabla, diseno.Alto, negrita: true, alineacion: ParagraphAlignment.Center);
            celda.AddText(columnas[i].ToUpperInvariant());
        }

        foreach (var fila in datos.Filas)
        {
            var renglon = tabla.AddRow();
            renglon.VerticalAlignment = VerticalAlignment.Top;
            AgregarTexto(Parrafo(renglon.Cells[0].AddParagraph(), diseno.CeldaTabla, diseno.Alto, negrita: true, alineacion: ParagraphAlignment.Center), fila.Destino);
            AgregarTexto(Parrafo(renglon.Cells[1].AddParagraph(), diseno.CeldaTabla, diseno.Alto), fila.VehiculoPeso);
            AgregarTexto(Parrafo(renglon.Cells[2].AddParagraph(), diseno.CeldaTabla, diseno.Alto, Primario, negrita: true, ParagraphAlignment.Center), fila.Tarifa);
            AgregarTexto(Parrafo(renglon.Cells[3].AddParagraph(), diseno.NotaCelda, diseno.Alto, Atenuado), fila.Nota);
        }
    }

    private static void Franja(Row fila, string texto, Color fondo, double px, double relleno, clsDisenoPdf diseno)
    {
        fila.Shading.Color = fondo;
        fila.TopPadding = Px(relleno);
        fila.BottomPadding = Px(relleno);
        var celda = fila.Cells[0];
        celda.MergeRight = 3;
        AgregarTexto(Parrafo(celda.AddParagraph(), px, diseno.Alto, Blanco, negrita: true, ParagraphAlignment.Center), texto);
    }

    private static void NotaGeneral(Section seccion, string nota, clsDisenoPdf diseno)
    {
        var parrafo = Parrafo(seccion.AddParagraph(), diseno.NotaGeneral, diseno.Alto, Atenuado, alineacion: ParagraphAlignment.Justify, antes: diseno.MargenNotaGeneral);
        parrafo.AddFormattedText(clsTextosEstandar.EtiquetaNota, TextFormat.Bold);
        parrafo.AddText(" ");
        AgregarTexto(parrafo, nota);
    }

    /// <summary>
    /// Nota resaltada (<c>.nota-decreto</c>): fondo gris claro con borde izquierdo azul de 3 px y la etiqueta en negrita azul. La usan la
    /// nota de decretos y la aclaración de la cotización en dólares.
    /// </summary>
    private static void NotaResaltada(Section seccion, string? etiqueta, string nota, clsDisenoPdf diseno)
    {
        Espacio(seccion, 12);
        var tabla = seccion.AddTable();
        tabla.Borders.Visible = false;
        tabla.AddColumn(Mm(diseno.AnchoUtilMm));
        tabla.TopPadding = Px(8);
        tabla.BottomPadding = Px(8);
        tabla.LeftPadding = Px(10);
        tabla.RightPadding = Px(10);
        var celda = tabla.AddRow().Cells[0];
        celda.Shading.Color = Fondo;
        celda.Borders.Left.Visible = true;
        celda.Borders.Left.Width = Px(3);
        celda.Borders.Left.Color = Primario;
        var parrafo = Parrafo(celda.AddParagraph(), 10, diseno.Alto);
        if (etiqueta is not null)
        {
            parrafo.AddFormattedText(etiqueta, TextFormat.Bold).Color = Primario;
            parrafo.AddText(" ");
        }

        AgregarTexto(parrafo, nota);
        Espacio(seccion, 12);
    }

    /// <summary>Tabla clave-valor (<c>table.kv</c>): etiqueta atenuada al 40 % y línea inferior.</summary>
    private static void AgregarDatos(Section seccion, clsBloqueDocumento bloque, clsDisenoPdf diseno)
    {
        var tabla = TablaSinBordes(seccion.AddTable(), diseno.AnchoUtilMm, 40, 60);
        Relleno(tabla, diseno.RellenoDato.Vertical, diseno.RellenoDato.Horizontal);
        foreach (var dato in bloque.Datos)
        {
            var fila = tabla.AddRow();
            fila.Borders.Bottom.Visible = true;
            fila.Borders.Bottom.Width = 0.75;
            fila.Borders.Bottom.Color = Borde;
            AgregarTexto(Parrafo(fila.Cells[0].AddParagraph(), diseno.DatoBloque, diseno.Alto, Atenuado), dato.Etiqueta);
            AgregarTexto(Parrafo(fila.Cells[1].AddParagraph(), diseno.DatoBloque, diseno.Alto), dato.Valor);
        }

        Espacio(seccion, 6);
    }

    /// <summary>Dos columnas (<c>.cols</c>): "Estas tarifas incluyen" y "Estas tarifas no incluyen" con viñetas.</summary>
    private static void AgregarListas(Section seccion, clsDocumentoCotizacion documento, clsDisenoPdf diseno)
    {
        var separacion = Px(diseno.SeparacionColumnas).Millimeter;
        var columna = (diseno.AnchoUtilMm - separacion) / 2;
        var tabla = TablaSinBordes(seccion.AddTable(), diseno.AnchoUtilMm, columna, separacion, columna);
        Relleno(tabla, 0, 0);
        tabla.TopPadding = Px(8);
        var fila = tabla.AddRow();
        fila.VerticalAlignment = VerticalAlignment.Top;
        Lista(fila.Cells[0], clsTextosEstandar.TituloIncluye, documento.Incluye, diseno);
        Lista(fila.Cells[2], clsTextosEstandar.TituloNoIncluye, documento.NoIncluye, diseno);
    }

    private static void Lista(Cell celda, string titulo, IReadOnlyList<string> elementos, clsDisenoPdf diseno)
    {
        AgregarTexto(Parrafo(celda.AddParagraph(), diseno.H3, diseno.Alto, diseno.ColorH3, negrita: true, despues: diseno.MargenH3.Despues), titulo);
        foreach (var elemento in elementos)
        {
            Vinetado(celda.AddParagraph(), elemento, diseno.Lista, diseno.Alto, diseno.SangriaLista, diseno.EspacioLista);
        }
    }
}
