using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Paleta y medidas del CSS de <c>armar_cotizacion.py</c> / <c>armar_multi.py</c> en unidades de MigraDoc, más los
/// ayudantes de texto comunes a los bloques. Conversión: 1 px CSS = 0,75 pt; el interlineado del CSS (<c>line-height</c>)
/// se aplica como interlineado exacto (tamaño × factor), no como "múltiple" de MigraDoc (que es relativo a la altura de
/// línea de la fuente y daría líneas más altas).
/// </summary>
internal static class clsEstilosPdf
{
    // Tokens del design system de Botero Soto.
    public static readonly Color Primario = Hex("#005AA8");
    public static readonly Color Secundario = Hex("#009A44");
    public static readonly Color Acento = Hex("#0072CE");
    public static readonly Color Fondo = Hex("#F5F7FA");
    public static readonly Color Borde = Hex("#D9DEE5");
    public static readonly Color Texto = Hex("#1A2A3A");
    public static readonly Color Atenuado = Hex("#5F6B7A");
    public static readonly Color Blanco = Hex("#FFFFFF");

    /// <summary>Azul de los subtítulos y del enlace de cierre en la variante multi (<c>#0057B8</c>).</summary>
    public static readonly Color AzulMulti = Hex("#0057B8");

    /// <summary>Carácter de viñeta (en Liberation Sans).</summary>
    public const string Vineta = "•";

    public static Unit Px(double px) => Unit.FromPoint(px * 0.75);

    public static Unit Mm(double mm) => Unit.FromMillimeter(mm);

    /// <summary>Tamaño de letra en px CSS e interlineado exacto = tamaño × <paramref name="alto"/>.</summary>
    public static void Tamano(ParagraphFormat formato, double px, double alto)
    {
        formato.Font.Size = Px(px);
        formato.LineSpacingRule = LineSpacingRule.Exactly;
        formato.LineSpacing = Px(px * alto);
    }

    /// <summary>Párrafo con tamaño, color, negrita, alineación y márgenes (px CSS).</summary>
    public static Paragraph Parrafo(
        Paragraph parrafo,
        double px,
        double alto,
        Color? color = null,
        bool negrita = false,
        ParagraphAlignment alineacion = ParagraphAlignment.Left,
        double antes = 0,
        double despues = 0)
    {
        Tamano(parrafo.Format, px, alto);
        parrafo.Format.Font.Bold = negrita;
        parrafo.Format.Font.Color = color ?? Texto;
        parrafo.Format.Alignment = alineacion;
        parrafo.Format.SpaceBefore = Px(antes);
        parrafo.Format.SpaceAfter = Px(despues);
        return parrafo;
    }

    /// <summary>Agrega el texto; los saltos de línea del texto pasan a saltos de línea del PDF.</summary>
    public static void AgregarTexto(Paragraph parrafo, string texto, bool negrita = false, Color? color = null)
    {
        var lineas = texto.Split('\n');
        for (var i = 0; i < lineas.Length; i++)
        {
            if (i > 0)
            {
                parrafo.AddLineBreak();
            }

            if (lineas[i].Length == 0)
            {
                continue;
            }

            var formateado = parrafo.AddFormattedText(lineas[i]);
            if (negrita)
            {
                formateado.Bold = true; // si no, hereda la negrita del párrafo (no se fuerza a false)
            }

            if (color is { } c)
            {
                formateado.Color = c;
            }
        }
    }

    /// <summary>Elemento de lista con viñeta propia y sangría francesa (<c>ul li</c> con <c>margin-left</c> en px).</summary>
    public static Paragraph Vinetado(Paragraph parrafo, string texto, double px, double alto, double sangriaPx, double despuesPx, Color? color = null)
    {
        Parrafo(parrafo, px, alto, color, despues: despuesPx);
        parrafo.Format.LeftIndent = Px(sangriaPx);
        parrafo.Format.FirstLineIndent = Px(-sangriaPx * 0.75);
        parrafo.Format.TabStops.ClearAll();
        parrafo.Format.TabStops.AddTabStop(Px(sangriaPx));
        parrafo.AddText(Vineta);
        parrafo.AddTab();
        AgregarTexto(parrafo, texto);
        return parrafo;
    }

    /// <summary>Espacio vertical de <paramref name="px"/> (margen CSS entre bloques; una tabla no tiene "SpaceBefore").</summary>
    public static void Espacio(Section seccion, double px)
    {
        var parrafo = seccion.AddParagraph();
        parrafo.Format.Font.Size = 1;
        parrafo.Format.LineSpacingRule = LineSpacingRule.Exactly;
        parrafo.Format.LineSpacing = Px(px);
        parrafo.Format.SpaceBefore = 0;
        parrafo.Format.SpaceAfter = 0;
    }

    /// <summary>Relleno de una tabla en px CSS (arriba/abajo, izquierda/derecha).</summary>
    public static void Relleno(Table tabla, double vertical, double horizontal)
    {
        tabla.TopPadding = Px(vertical);
        tabla.BottomPadding = Px(vertical);
        tabla.LeftPadding = Px(horizontal);
        tabla.RightPadding = Px(horizontal);
    }

    /// <summary>Tabla sin bordes que ocupa <paramref name="anchoMm"/> repartido en columnas proporcionales.</summary>
    public static Table TablaSinBordes(Table tabla, double anchoMm, params double[] proporciones)
    {
        tabla.Borders.Visible = false;
        var total = proporciones.Sum();
        foreach (var proporcion in proporciones)
        {
            tabla.AddColumn(Mm(anchoMm * proporcion / total));
        }

        return tabla;
    }

    private static Color Hex(string hex) =>
        new(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

/// <summary>
/// Medidas de cada variante (CSS de <c>armar_cotizacion.py</c> y <c>armar_multi.py</c>, en px salvo donde dice mm).
/// </summary>
internal sealed record clsDisenoPdf
{
    public required double MargenSuperiorMm { get; init; }
    public required double MargenLateralMm { get; init; }
    public required double MargenInferiorMm { get; init; }

    /// <summary>Distancia del borde superior de la hoja al logo (margen superior + <c>top</c> negativo del CSS).</summary>
    public required double EncabezadoMm { get; init; }

    public required double AnchoLogoMm { get; init; }

    /// <summary>Distancia del borde inferior al pie (margen inferior + <c>bottom</c> negativo del CSS).</summary>
    public required double PieMm { get; init; }

    public required bool FranjaWeb { get; init; }
    public required double Cuerpo { get; init; }
    public required double Alto { get; init; }
    public required double H1 { get; init; }
    public required (double Antes, double Despues) MargenH1 { get; init; }
    public required double H2 { get; init; }
    public required (double Antes, double Despues) MargenH2 { get; init; }
    public required double H3 { get; init; }
    public required (double Antes, double Despues) MargenH3 { get; init; }
    public required Color ColorH3 { get; init; }
    public required double Parrafo { get; init; }
    public required double EspacioParrafo { get; init; }
    public required double Cliente { get; init; }
    public required double MargenCliente { get; init; }
    public required double MargenAsunto { get; init; }
    public required double MargenTabla { get; init; }
    public required double TituloTabla { get; init; }
    public required double RellenoTitulo { get; init; }
    public required double OrigenTabla { get; init; }
    public required double RellenoOrigen { get; init; }
    public required double EncabezadoTabla { get; init; }
    public required double RellenoEncabezado { get; init; }
    public required double CeldaTabla { get; init; }
    public required (double Vertical, double Horizontal) RellenoCelda { get; init; }
    public required double NotaCelda { get; init; }
    public required double NotaGeneral { get; init; }
    public required double MargenNotaGeneral { get; init; }
    public required double SeparacionColumnas { get; init; }
    public required double Lista { get; init; }
    public required double SangriaLista { get; init; }
    public required double EspacioLista { get; init; }
    public required double DatoBloque { get; init; }
    public required (double Vertical, double Horizontal) RellenoDato { get; init; }
    public required double MargenFirma { get; init; }
    public required double MargenNombreFirma { get; init; }
    public required double DetalleFirma { get; init; }

    /// <summary>Tarifa única: A4 34/16/30/16 mm, cuerpo 12 px con interlineado 1,5.</summary>
    public static readonly clsDisenoPdf TarifaUnica = new()
    {
        MargenSuperiorMm = 34, MargenLateralMm = 16, MargenInferiorMm = 30,
        EncabezadoMm = 10, AnchoLogoMm = 21.2, PieMm = 6, FranjaWeb = true,
        Cuerpo = 12, Alto = 1.5,
        H1 = 14, MargenH1 = (14, 8), H2 = 14, MargenH2 = (14, 6), H3 = 11, MargenH3 = (10, 4), ColorH3 = clsEstilosPdf.Texto,
        Parrafo = 12, EspacioParrafo = 8, Cliente = 13, MargenCliente = 10, MargenAsunto = 10,
        MargenTabla = 6, TituloTabla = 11, RellenoTitulo = 6, OrigenTabla = 10, RellenoOrigen = 5,
        EncabezadoTabla = 9, RellenoEncabezado = 6, CeldaTabla = 10, RellenoCelda = (7, 8), NotaCelda = 9,
        NotaGeneral = 9, MargenNotaGeneral = 8, SeparacionColumnas = 24, Lista = 10, SangriaLista = 16, EspacioLista = 3,
        DatoBloque = 10, RellenoDato = (4, 6), MargenFirma = 22, MargenNombreFirma = 18, DetalleFirma = 10,
    };

    /// <summary>Multi-sección: A4 34/14/28/14 mm, cuerpo 10,5 px con interlineado 1,45 y logo de 42 mm.</summary>
    public static readonly clsDisenoPdf MultiSeccion = new()
    {
        MargenSuperiorMm = 34, MargenLateralMm = 14, MargenInferiorMm = 28,
        EncabezadoMm = 8, AnchoLogoMm = 42, PieMm = 6, FranjaWeb = false,
        Cuerpo = 10.5, Alto = 1.45,
        H1 = 13, MargenH1 = (8, 4), H2 = 10.5, MargenH2 = (6, 2), H3 = 10, MargenH3 = (6, 2), ColorH3 = clsEstilosPdf.AzulMulti,
        Parrafo = 10, EspacioParrafo = 4, Cliente = 12, MargenCliente = 8, MargenAsunto = 7,
        MargenTabla = 7, TituloTabla = 10, RellenoTitulo = 4, OrigenTabla = 9, RellenoOrigen = 3,
        EncabezadoTabla = 8, RellenoEncabezado = 4, CeldaTabla = 9, RellenoCelda = (4, 6), NotaCelda = 8,
        NotaGeneral = 8, MargenNotaGeneral = 3, SeparacionColumnas = 16, Lista = 9, SangriaLista = 14, EspacioLista = 2,
        DatoBloque = 9, RellenoDato = (3, 5), MargenFirma = 8, MargenNombreFirma = 6, DetalleFirma = 9,
    };

    /// <summary>Ancho útil de la hoja A4 (210 mm menos los márgenes laterales).</summary>
    public double AnchoUtilMm => 210 - (2 * MargenLateralMm);
}
