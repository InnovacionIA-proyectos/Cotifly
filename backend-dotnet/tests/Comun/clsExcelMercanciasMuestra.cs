using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Excel de mercancías de una ruta para las pruebas de HU-035.1 (#202): uno normal (hoja "Mercancías", encabezado en la fila 1 y las
/// filas desde la 2), variantes con fórmula y fecha, y archivos corruptos o maliciosos (texto renombrado, zip sin libro, bomba zip),
/// todos generados en la propia prueba.
/// </summary>
public static class clsExcelMercanciasMuestra
{
    public const string Hoja = "Mercancías";

    public static readonly string[] Encabezados = ["Tipo de mercancía", "Cantidad", "Largo (m)", "Ancho (m)", "Alto (m)", "Peso (kg)"];

    private static readonly (string Tipo, int Cantidad, double Largo, double Ancho, double Alto, double Peso)[] _mercancias =
    [
        ("Cafe en estiba", 10, 1.2, 1, 1.5, 850.5), ("Bobinas de acero", 2, 3, 2, 1, 2000), ("Cajas de electrodomésticos", 40, 0.8, 0.6, 0.9, 25),
    ];

    /// <summary>Mercancía <paramref name="i"/> (base 0), cíclica.</summary>
    public static (string Tipo, int Cantidad, double Largo, double Ancho, double Alto, double Peso) Mercancia(int i) => _mercancias[i % _mercancias.Length];

    /// <summary>Excel con la hoja "Mercancías" de <paramref name="filas"/> filas de datos (desde la fila 2).</summary>
    public static byte[] Excel(int filas = 3) => Guardar(libro =>
    {
        var ws = libro.Worksheets.Add(Hoja);
        for (var c = 0; c < Encabezados.Length; c++)
        {
            ws.Cell(1, c + 1).Value = Encabezados[c];
        }

        for (var i = 0; i < filas; i++)
        {
            var (tipo, cantidad, largo, ancho, alto, peso) = Mercancia(i);
            ws.Cell(i + 2, 1).Value = tipo;
            ws.Cell(i + 2, 2).Value = cantidad;
            ws.Cell(i + 2, 3).Value = largo;
            ws.Cell(i + 2, 4).Value = ancho;
            ws.Cell(i + 2, 5).Value = alto;
            ws.Cell(i + 2, 6).Value = peso;
        }
    });

    /// <summary>
    /// El Excel normal de <paramref name="filas"/> filas más una hoja "Listas" oculta (<c>hidden</c>) antes de "Mercancías", como las listas
    /// de validación que dejan muchos clientes.
    /// </summary>
    public static byte[] ConHojaOculta(int filas = 3)
    {
        using var memoria = new MemoryStream(Excel(filas));
        using var libro = new XLWorkbook(memoria);
        var listas = libro.Worksheets.Add("Listas", 1);
        listas.Cell("A1").Value = "Tipo";
        listas.Cell("B1").Value = "Unidad";
        listas.Cell("C1").Value = "Grupo";
        listas.Cell("A2").Value = "Caja";
        listas.Visibility = XLWorksheetVisibility.Hidden;
        using var salida = new MemoryStream();
        libro.SaveAs(salida);
        return salida.ToArray();
    }

    /// <summary>
    /// Una fila de bobinas con el peso como fórmula (<c>=B2*1000</c>, con el valor 2000 guardado, como lo deja Excel) y una fecha de
    /// despacho (2026-10-20) con formato de fecha colombiano.
    /// </summary>
    public static byte[] ConFormulaYFecha()
    {
        var libro = Guardar(l =>
        {
            var ws = l.Worksheets.Add(Hoja);
            string[] encabezados = [.. Encabezados, "Fecha de despacho"];
            for (var c = 0; c < encabezados.Length; c++)
            {
                ws.Cell(1, c + 1).Value = encabezados[c];
            }

            ws.Cell(2, 1).Value = "Bobinas";
            ws.Cell(2, 2).Value = 2;
            ws.Cell(2, 3).Value = 3;
            ws.Cell(2, 4).Value = 2;
            ws.Cell(2, 5).Value = 1;
            ws.Cell(2, 6).Value = 2000;
            ws.Cell(2, 7).Value = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Unspecified);
            ws.Cell(2, 7).Style.DateFormat.Format = "dd/mm/yyyy";
        });
        return ConFormula(libro, Hoja, "F2", "B2*1000", conValorGuardado: true);
    }

    /// <summary>
    /// Pone la fórmula <paramref name="formula"/> en la celda <paramref name="celda"/> (que ya tiene un valor) editando el XML: con
    /// <paramref name="conValorGuardado"/> conserva el valor como el que Excel guardó; sin él, la deja sin valor (un generador que no calcula).
    /// </summary>
    public static byte[] ConFormula(byte[] libro, string hoja, string celda, string formula, bool conValorGuardado)
    {
        ArgumentNullException.ThrowIfNull(libro);
        using var memoria = new MemoryStream();
        memoria.Write(libro);
        using (var documento = SpreadsheetDocument.Open(memoria, true))
        {
            var parte = documento.WorkbookPart!;
            var hojaXml = parte.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == hoja);
            var worksheet = ((WorksheetPart)parte.GetPartById(hojaXml.Id!)).Worksheet;
            var c = worksheet.Descendants<Cell>().Single(x => x.CellReference == celda);
            c.CellFormula = new CellFormula(formula);
            if (!conValorGuardado)
            {
                c.CellValue?.Remove();
            }

            worksheet.Save();
        }

        return memoria.ToArray();
    }

    /// <summary>Un libro con una sola hoja vacía.</summary>
    public static byte[] SinDatos() => Guardar(l => l.Worksheets.Add("Vacía"));

    /// <summary>
    /// HU-035.2 (#213), validación: <paramref name="xlsx"/> convertido en <c>.xlsm</c> con macros por el SDK de Open XML, que deja el libro
    /// en <c>xl/workbook2.xml</c> (no en <c>xl/workbook.xml</c> como Excel): un libro válido que ClosedXML abre por la relación
    /// <c>officeDocument</c>.
    /// </summary>
    public static byte[] XlsmConLibroEnOtraParte(byte[] xlsx)
    {
        using var memoria = new MemoryStream();
        memoria.Write(xlsx);
        using (var documento = SpreadsheetDocument.Open(memoria, true))
        {
            documento.ChangeDocumentType(DocumentFormat.OpenXml.SpreadsheetDocumentType.MacroEnabledWorkbook);
            var vba = documento.WorkbookPart!.AddNewPart<VbaProjectPart>();
            using var macros = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
            vba.FeedData(macros);
        }

        return memoria.ToArray();
    }

    /// <summary>Un archivo de texto con extensión <c>.xlsx</c>.</summary>
    public static byte[] TextoRenombrado() => Encoding.UTF8.GetBytes("tipo;cantidad;peso\ncafe;10;850\n");

    /// <summary>Un zip válido que no es un libro de Excel (no tiene <c>xl/workbook.xml</c>).</summary>
    public static byte[] ZipSinLibro() => Zip(("hola.txt", 100));

    /// <summary>
    /// Bomba zip: un "libro" cuya hoja descomprime a <paramref name="megabytes"/> MB (por defecto 80, más que los 64 del tope) y ocupa
    /// unos pocos KB comprimida.
    /// </summary>
    public static byte[] BombaZip(int megabytes = 80) =>
        Zip(("[Content_Types].xml", 200), ("xl/workbook.xml", 200), ("xl/worksheets/sheet1.xml", megabytes * 1024 * 1024));

    /// <summary>Un Excel válido truncado a la mitad (corrupto).</summary>
    public static byte[] Truncado()
    {
        var excel = Excel(3);
        return excel[..(excel.Length / 2)];
    }

    /// <summary>Un zip con <c>xl/workbook.xml</c> que no es XML: abre como zip, pero ClosedXML no puede leerlo.</summary>
    public static byte[] LibroIlegible() => Zip(("[Content_Types].xml", 50), ("xl/workbook.xml", 50));

    /// <summary>
    /// Libro denso (validación de HU-035.1): la hoja "Mercancías" con <paramref name="celdas"/> celdas numéricas mínimas
    /// (<c>&lt;c&gt;&lt;v&gt;1&lt;/v&gt;&lt;/c&gt;</c>, sin referencia, 50 por fila). Comprime a muy poco: 4 millones de celdas son unos
    /// 200 KB.
    /// </summary>
    public static byte[] Densa(int celdas)
    {
        var xml = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var hechas = 0; hechas < celdas; hechas += 50)
        {
            xml.Append("<row>");
            for (var c = 0; c < Math.Min(50, celdas - hechas); c++)
            {
                xml.Append("<c><v>1</v></c>");
            }

            xml.Append("</row>");
        }

        return ConHojaXml(Excel(1), xml.Append("</sheetData></worksheet>").ToString());
    }

    /// <summary>
    /// XXE (validación de HU-035.1): la hoja declara una entidad externa (<c>file:///etc/passwd</c>) y la usa en una celda. Un lector que
    /// resolviera entidades devolvería el archivo del servidor.
    /// </summary>
    public static byte[] ConEntidadExterna() => ConHojaXml(Excel(1),
        "<?xml version=\"1.0\"?><!DOCTYPE w [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>"
        + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">"
        + "<c r=\"A1\" t=\"inlineStr\"><is><t>&e;</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>b</t></is></c>"
        + "<c r=\"C1\" t=\"inlineStr\"><is><t>c</t></is></c></row></sheetData></worksheet>");

    /// <summary>
    /// Cambia el XML de la primera hoja (<c>xl/worksheets/sheet1.xml</c>) de <paramref name="libro"/> por <paramref name="xmlHoja"/>,
    /// copiando el resto del paquete tal cual.
    /// </summary>
    public static byte[] ConHojaXml(byte[] libro, string xmlHoja) => ConEntrada(libro, "xl/worksheets/sheet1.xml", _ => xmlHoja);

    /// <summary>
    /// HU-035.3 (#277): <paramref name="xlsx"/> con la configuración de impresión que guarda Excel al hacer «Configurar página» en cada
    /// hoja: orientación, papel, área de impresión y títulos (nombres definidos <c>_xlnm.Print_Area</c> y <c>_xlnm.Print_Titles</c>),
    /// encabezado y pie, saltos de página, ajuste a una página, márgenes, opciones de impresión y, como Excel, la parte binaria
    /// <c>xl/printerSettings/printerSettingsN.bin</c> (un DEVMODE ficticio) enlazada desde <c>pageSetup r:id</c>.
    /// </summary>
    public static byte[] ConConfiguracionDeImpresion(byte[] xlsx)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        using var memoria = new MemoryStream();
        using (var libro = new XLWorkbook(new MemoryStream(xlsx)))
        {
            foreach (var hoja in libro.Worksheets)
            {
                var pagina = hoja.PageSetup;
                pagina.PageOrientation = XLPageOrientation.Landscape;
                pagina.PaperSize = XLPaperSize.LetterPaper;
                pagina.PrintAreas.Add("A1:H40");
                pagina.SetRowsToRepeatAtTop(1, 1);
                pagina.Header.Center.AddText("Pliego de prueba");
                pagina.Footer.Right.AddText(XLHFPredefinedText.PageNumber);
                pagina.AddHorizontalPageBreak(20);
                pagina.AddVerticalPageBreak(4);
                pagina.FitToPages(1, 0);
                pagina.Margins.Left = 0.4;
                pagina.CenterHorizontally = true;
                pagina.ShowGridlines = true;
            }

            libro.SaveAs(memoria);
        }

        using (var documento = SpreadsheetDocument.Open(memoria, true))
        {
            foreach (var hoja in documento.WorkbookPart!.WorksheetParts)
            {
                var ajustes = hoja.AddNewPart<SpreadsheetPrinterSettingsPart>();
                using (var devmode = new MemoryStream(DevmodeFicticio()))
                {
                    ajustes.FeedData(devmode);
                }

                var pageSetup = hoja.Worksheet!.GetFirstChild<PageSetup>()
                    ?? throw new InvalidOperationException("ClosedXML no escribió pageSetup.");
                pageSetup.Id = hoja.GetIdOfPart(ajustes);
                hoja.Worksheet.Save();
            }
        }

        return memoria.ToArray();
    }

    /// <summary>
    /// HU-035.3 (#277): <paramref name="xlsx"/> con una parte más, relacionada desde el libro, de tipo <paramref name="tipo"/> y contenido
    /// <paramref name="datos"/> (partes binarias que guarda Excel, o partes XML maliciosas).
    /// </summary>
    public static byte[] ConParteExtra(byte[] xlsx, string tipo, byte[] datos)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        using var memoria = new MemoryStream();
        memoria.Write(xlsx);
        using (var documento = SpreadsheetDocument.Open(memoria, true))
        {
            var parte = documento.WorkbookPart!.AddExtendedPart("http://cotifly.test/relaciones/extra", tipo, "dat");
            using var flujo = new MemoryStream(datos);
            parte.FeedData(flujo);
        }

        return memoria.ToArray();
    }

    /// <summary>
    /// HU-035.3 (#277): cambia en <c>[Content_Types].xml</c> el tipo de la primera hoja por <paramref name="tipo"/> (un libro que intenta
    /// que su hoja no se cuente como XML).
    /// </summary>
    public static byte[] ConTipoDeHoja(byte[] libro, string tipo) =>
        ConTipoDeParte(libro, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", tipo);

    /// <summary>
    /// HU-035.3 (#277), auditoría: cambia en <c>[Content_Types].xml</c> el tipo de la parte principal (<c>xl/workbook.xml</c>) por
    /// <paramref name="tipo"/>. El SDK de Open XML no fija el tipo del libro y lo abre igual.
    /// </summary>
    public static byte[] ConTipoDeLibro(byte[] libro, string tipo) =>
        ConTipoDeParte(libro, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", tipo);

    /// <summary>
    /// HU-035.3 (#277), auditoría: <paramref name="libro"/> con <paramref name="extra"/> elementos vacíos más dentro de
    /// <c>xl/workbook.xml</c>, antes de su cierre (un libro principal denso).
    /// </summary>
    public static byte[] ConLibroDenso(byte[] libro, int extra) => ConEntrada(libro, "xl/workbook.xml", xml =>
        xml.Replace("</x:workbook>", string.Concat(Enumerable.Repeat("<x:z/>", extra)) + "</x:workbook>", StringComparison.Ordinal)
           .Replace("</workbook>", string.Concat(Enumerable.Repeat("<z/>", extra)) + "</workbook>", StringComparison.Ordinal));

    /// <summary>HU-035.3 (#277), auditoría: <c>xl/workbook.xml</c> de <paramref name="libro"/> con un DTD y una entidad externa.</summary>
    public static byte[] ConLibroConDtd(byte[] libro) => ConEntrada(libro, "xl/workbook.xml", xml =>
        "<?xml version=\"1.0\"?><!DOCTYPE w [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>"
        + xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..].TrimStart('﻿'));

    /// <summary>
    /// Cambia el tipo de <paramref name="parte"/> en <c>[Content_Types].xml</c>: en su <c>Override</c> o, si el paquete la tipa por el
    /// <c>Default</c> de su extensión (System.IO.Packaging lo hace con el libro), agregándole un <c>Override</c>, que manda sobre el
    /// <c>Default</c>.
    /// </summary>
    private static byte[] ConTipoDeParte(byte[] libro, string parte, string tipoActual, string tipo) =>
        ConEntrada(libro, "[Content_Types].xml", xml =>
        {
            var documento = System.Xml.Linq.XDocument.Parse(xml);
            var raiz = documento.Root!;
            var extension = parte[(parte.LastIndexOf('.') + 1)..];
            var sobrescritura = raiz.Elements().SingleOrDefault(e => e.Name.LocalName == "Override" && (string?)e.Attribute("PartName") == parte);
            if (sobrescritura is not null && (string?)sobrescritura.Attribute("ContentType") == tipoActual)
            {
                sobrescritura.SetAttributeValue("ContentType", tipo);
            }
            else if (sobrescritura is null && raiz.Elements().Any(e => e.Name.LocalName == "Default"
                && string.Equals((string?)e.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)
                && (string?)e.Attribute("ContentType") == tipoActual))
            {
                raiz.Add(new System.Xml.Linq.XElement(raiz.Name.Namespace + "Override",
                    new System.Xml.Linq.XAttribute("PartName", parte), new System.Xml.Linq.XAttribute("ContentType", tipo)));
            }
            else
            {
                throw new InvalidOperationException($"No se encontró el tipo de {parte} en [Content_Types].xml.");
            }

            return documento.Declaration + documento.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        });

    /// <summary>Un DEVMODE ficticio: nombre de impresora en UTF-16 relleno a 64 bytes y campos binarios (no es XML).</summary>
    public static byte[] DevmodeFicticio()
    {
        var datos = new byte[220];
        Encoding.Unicode.GetBytes("Impresora ficticia").CopyTo(datos, 0);
        for (var i = 64; i < datos.Length; i++)
        {
            datos[i] = (byte)(i * 37);
        }

        return datos;
    }

    /// <summary>Copia <paramref name="libro"/> cambiando el texto de la entrada <paramref name="nombre"/> con <paramref name="cambio"/>.</summary>
    private static byte[] ConEntrada(byte[] libro, string nombre, Func<string, string> cambio)
    {
        ArgumentNullException.ThrowIfNull(libro);
        using var salida = new MemoryStream();
        using (var origen = new ZipArchive(new MemoryStream(libro), ZipArchiveMode.Read))
        using (var destino = new ZipArchive(salida, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entrada in origen.Entries)
            {
                using var escritura = destino.CreateEntry(entrada.FullName, CompressionLevel.SmallestSize).Open();
                using var lectura = entrada.Open();
                if (entrada.FullName == nombre)
                {
                    using var texto = new StreamReader(lectura, Encoding.UTF8);
                    escritura.Write(Encoding.UTF8.GetBytes(cambio(texto.ReadToEnd())));
                }
                else
                {
                    lectura.CopyTo(escritura);
                }
            }
        }

        return salida.ToArray();
    }

    private static byte[] Zip(params (string Nombre, int Bytes)[] entradas)
    {
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            var bloque = new byte[64 * 1024];
            Array.Fill(bloque, (byte)' ');
            foreach (var (nombre, bytes) in entradas)
            {
                using var flujo = zip.CreateEntry(nombre, CompressionLevel.SmallestSize).Open();
                for (var escritos = 0; escritos < bytes; escritos += bloque.Length)
                {
                    flujo.Write(bloque, 0, Math.Min(bloque.Length, bytes - escritos));
                }
            }
        }

        return memoria.ToArray();
    }

    private static byte[] Guardar(Action<XLWorkbook> armar)
    {
        using var libro = new XLWorkbook();
        armar(libro);
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }
}
