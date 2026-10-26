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
    public static byte[] ConHojaXml(byte[] libro, string xmlHoja)
    {
        ArgumentNullException.ThrowIfNull(libro);
        using var salida = new MemoryStream();
        using (var origen = new ZipArchive(new MemoryStream(libro), ZipArchiveMode.Read))
        using (var destino = new ZipArchive(salida, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entrada in origen.Entries)
            {
                using var escritura = destino.CreateEntry(entrada.FullName, CompressionLevel.SmallestSize).Open();
                if (entrada.FullName == "xl/worksheets/sheet1.xml")
                {
                    escritura.Write(Encoding.UTF8.GetBytes(xmlHoja));
                }
                else
                {
                    using var lectura = entrada.Open();
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
