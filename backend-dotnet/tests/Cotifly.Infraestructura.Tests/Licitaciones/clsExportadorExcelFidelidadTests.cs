using System.Diagnostics;
using System.IO.Compression;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Pruebas.Comun;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Exportación (HU-011.3) contra originales difíciles: un .xlsm sale como .xlsx real, textos que empiezan con "=" no son fórmulas,
/// se conservan fórmulas, formatos, combinadas, hojas ocultas, validaciones, comentarios, nombres y tablas del cliente, no se pisan
/// datos a la derecha, una fila de totales no queda "Sin enviar", 1000 líneas en tiempo razonable, una línea sin fila en el pliego
/// aparece en el resumen y ninguna parte del paquete trae costos.
/// </summary>
public sealed class clsExportadorExcelFidelidadTests
{
    private static clsExportacionLicitacion Datos(byte[]? original, string hoja = clsPliegoMuestra.Hoja, string? cliente = "Agencia JF",
        IReadOnlyList<clsLineaExportacion>? lineas = null, IReadOnlyList<clsConexoExportacion>? conexos = null) => new(
        "PCN-900555111-DG-01102026-001", cliente, "900555111", "COP", 0m, null, hoja, original,
        lineas ??
        [
            new clsLineaExportacion(1, 0, "BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32000m, "Costeada", 1_176_471m),
            new clsLineaExportacion(2, 2, "CUCUTA", "BOGOTA, D.C.", "2", 8000m, "Pendiente de costeo", null),
        ],
        conexos ?? [new clsConexoExportacion("Cargue", "logistico", 1m, 200_000m)],
        new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));

    private static byte[] Guardar(XLWorkbook libro)
    {
        using var m = new MemoryStream();
        libro.SaveAs(m);
        return m.ToArray();
    }

    [Fact]
    public void UnXlsmOriginalSaleComoXlsxDeVerdadSinMacros()
    {
        var xlsx = clsPliegoMuestra.Excel(3);
        using var m = new MemoryStream();
        m.Write(xlsx);
        using (var doc = SpreadsheetDocument.Open(m, true))
        {
            doc.ChangeDocumentType(SpreadsheetDocumentType.MacroEnabledWorkbook);
            var vba = doc.WorkbookPart!.AddNewPart<VbaProjectPart>();
            using var s = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
            vba.FeedData(s);
        }

        var exportado = clsExportadorExcelLicitacionClosedXml.Generar(Datos(m.ToArray()));
        exportado.EsExitoso.Should().BeTrue(exportado.EsFallo ? exportado.Error.Mensaje : "");
        using var zip = new ZipArchive(new MemoryStream(exportado.Valor));
        using var tipos = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
        var contenido = tipos.ReadToEnd();
        var nombres = zip.Entries.Select(e => e.FullName).ToList();
        contenido.Should().NotContain("macroEnabled", "el archivo se entrega como .xlsx");
        nombres.Should().NotContain(n => n.Contains("vbaProject", StringComparison.OrdinalIgnoreCase), "las macros no se conservan");
    }

    [Fact]
    public void LosTextosQueEmpiezanConIgualNoSeVuelvenFormulas()
    {
        var datos = Datos(clsPliegoMuestra.Excel(3), cliente: "=HYPERLINK(\"http://x\",\"a\")",
            conexos: [new clsConexoExportacion("=cmd|' /C calc'!A0", "x", 1m, 1m)]);
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));
        libro.Worksheets.SelectMany(h => h.CellsUsed()).Where(c => c.HasFormula).Should().BeEmpty();
        // also in raw xml: no <f> elements
        using var zip = new ZipArchive(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)))
        {
            using var r = new StreamReader(e.Open());
            r.ReadToEnd().Should().NotContain("<x:f>").And.NotContain("<f>");
        }
    }

    [Fact]
    public void SinOriginalUnaRutaConIgualNoEsFormula()
    {
        var datos = Datos(null, lineas: [new clsLineaExportacion(1, null, "=1+2", "@SUM(1)", "+x", null, "Costeada", 1m)]);
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));
        libro.Worksheets.SelectMany(h => h.CellsUsed()).Where(c => c.HasFormula).Should().BeEmpty();
    }

    [Fact]
    public void SeConservanFormulasFormatosValidacionesComentariosNombresYHojasOcultas()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        var ws = libro.Worksheet(clsPliegoMuestra.Hoja);
        ws.Cell(4, 6).FormulaA1 = "E4*2";
        ws.Cell(4, 6).Style.NumberFormat.Format = "0.00%";
        ws.Cell(5, 5).Style.Fill.BackgroundColor = XLColor.Red;
        ws.Cell(5, 2).GetComment().AddText("comentario cliente");
        ws.Range("D3:D7").CreateDataValidation().List("\"Sencillo,Tractomula\"");
        ws.Range("E4:E7").AddConditionalFormat().WhenGreaterThan(20).Fill.SetBackgroundColor(XLColor.Green);
        ws.SheetView.FreezeRows(3);
        libro.DefinedNames.Add("MiRango", ws.Range("B4:C7"));
        var oculta = libro.Worksheets.Add("Oculta");
        oculta.Cell(1, 1).Value = "secreto cliente";
        oculta.Visibility = XLWorksheetVisibility.Hidden;
        ws.Column(2).Width = 33;
        var original = Guardar(libro);

        var r = clsExportadorExcelLicitacionClosedXml.Generar(Datos(original));
        r.EsExitoso.Should().BeTrue();
        using var d = new XLWorkbook(new MemoryStream(r.Valor));
        var h = d.Worksheet(clsPliegoMuestra.Hoja);
        h.Cell(4, 6).FormulaA1.Should().Be("E4*2");
        h.Cell(4, 6).Style.NumberFormat.Format.Should().Be("0.00%");
        h.Cell(5, 5).Style.Fill.BackgroundColor.Should().Be(XLColor.Red);
        h.Cell(5, 2).HasComment.Should().BeTrue();
        h.DataValidations.Count().Should().Be(1);
        h.ConditionalFormats.Count().Should().Be(1);
        h.SheetView.SplitRow.Should().Be(3);
        h.Column(2).Width.Should().BeApproximately(33, 0.01);
        h.MergedRanges.Count.Should().BeGreaterThanOrEqualTo(1);
        d.DefinedNames.Contains("MiRango").Should().BeTrue();
        d.Worksheet("Oculta").Visibility.Should().Be(XLWorksheetVisibility.Hidden);
    }

    [Fact]
    public void LosDatosDelClienteALaDerechaNoSePisan()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        var ws = libro.Worksheet(clsPliegoMuestra.Hoja);
        ws.Cell(1, 12).Value = "Nota del cliente en L1";
        ws.Cell(20, 10).Value = "Pie en J20";
        var original = Guardar(libro);

        using var d = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(original)).Valor));
        var h = d.Worksheet(clsPliegoMuestra.Hoja);
        h.Cell(1, 12).GetString().Should().Be("Nota del cliente en L1");
        h.Cell(20, 10).GetString().Should().Be("Pie en J20");
        h.Cell(3, 13).GetString().Should().StartWith("Precio final Cotifly");
    }

    [Fact]
    public void UnaFilaDeTotalesDelClienteNoQuedaComoSinEnviar()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        var ws = libro.Worksheet(clsPliegoMuestra.Hoja);
        ws.Cell(9, 4).Value = "TOTAL";
        ws.Cell(9, 5).FormulaA1 = "SUM(E4:E8)";
        var original = Guardar(libro);
        using var d = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(original)).Valor));
        d.Worksheet(clsPliegoMuestra.Hoja).Cell(9, 8).GetString().Should().BeEmpty("la fila de totales no es una ruta");
    }

    [Fact]
    public void UnaTablaDeExcelDelClienteSigueValida()
    {
        using var libro = new XLWorkbook();
        var ws = libro.Worksheets.Add("Rutas");
        ws.Cell(1, 1).Value = "ITEM"; ws.Cell(1, 2).Value = "ORIGEN"; ws.Cell(1, 3).Value = "DESTINO";
        for (var i = 0; i < 3; i++) { ws.Cell(i + 2, 1).Value = i + 1; ws.Cell(i + 2, 2).Value = "A" + i; ws.Cell(i + 2, 3).Value = "B" + i; }
        ws.Range(1, 1, 4, 3).CreateTable("TablaRutas");
        var original = Guardar(libro);
        var r = clsExportadorExcelLicitacionClosedXml.Generar(Datos(original, hoja: "Rutas", lineas:
            [new clsLineaExportacion(1, 0, "A0", "B0", null, null, "Costeada", 10m), new clsLineaExportacion(2, 1, "A1", "B1", null, null, "Costeada", 20m)]));
        r.EsExitoso.Should().BeTrue();
        using var d = new XLWorkbook(new MemoryStream(r.Valor));
        d.Worksheet("Rutas").Tables.Count().Should().Be(1);
        d.Worksheet("Rutas").Cell(2, 4).GetValue<decimal>().Should().Be(10m);
    }

    [Fact]
    public void MilLineasSeExportanEnTiempoRazonable()
    {
        var original = clsPliegoMuestra.Excel(1000);
        var lineas = Enumerable.Range(0, 1000).Select(i => new clsLineaExportacion(i + 1, i, "A", "B", "3S3", 1000m, i % 3 == 0 ? "Pendiente de costeo" : "Costeada", i % 3 == 0 ? null : 1_000_000m + i)).ToList();
        var reloj = Stopwatch.StartNew();
        var memoriaAntes = GC.GetAllocatedBytesForCurrentThread();
        var r = clsExportadorExcelLicitacionClosedXml.Generar(Datos(original, lineas: lineas));
        reloj.Stop();
        var asignado = GC.GetAllocatedBytesForCurrentThread() - memoriaAntes;
        r.EsExitoso.Should().BeTrue();
        reloj.ElapsedMilliseconds.Should().BeLessThan(10_000);
        asignado.Should().BeLessThan(2L * 1024 * 1024 * 1024, "1000 líneas no deberían asignar más de 2 GB");
        using var d = new XLWorkbook(new MemoryStream(r.Valor));
        var hoja = d.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(clsPliegoMuestra.FilaExcel(998), 7).GetValue<decimal>().Should().Be(1_000_998m);
        hoja.Cell(clsPliegoMuestra.FilaExcel(999), 7).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.SinCosteo);
    }

    [Fact]
    public void UnaLineaConFilaFueraDelPliegoApareceEnElResumen()
    {
        var r = clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel(3), lineas:
            [new clsLineaExportacion(1, 0, "A", "B", null, null, "Costeada", 1m), new clsLineaExportacion(2, 5, "C", "D", null, null, "Costeada", 2m)]));
        using var d = new XLWorkbook(new MemoryStream(r.Valor));
        d.Worksheets.SelectMany(h => h.CellsUsed()).Should().Contain(c => c.DataType == XLDataType.Number && c.GetValue<decimal>() == 2m, "la línea 2 debe aparecer en algún lado");
    }

    [Fact]
    public void SiYaExisteUnaHojaResumenSeUsaOtroNombre()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        libro.Worksheets.Add("Resumen Cotifly").Cell(1, 1).Value = "del cliente";
        var r = clsExportadorExcelLicitacionClosedXml.Generar(Datos(Guardar(libro)));
        using var d = new XLWorkbook(new MemoryStream(r.Valor));
        d.Worksheet("Resumen Cotifly").Cell(1, 1).GetString().Should().Be("del cliente");
        d.Worksheets.Select(w => w.Name).Should().Contain("Resumen Cotifly 2");
    }

    [Fact]
    public void NingunaParteDelPaqueteTraeCostos()
    {
        var r = clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel(3)));
        using var zip = new ZipArchive(new MemoryStream(r.Valor));
        foreach (var e in zip.Entries)
        {
            using var s = new StreamReader(e.Open());
            var t = s.ReadToEnd();
            t.Should().NotContain("1000000").And.NotContain("170000");
        }
    }
}
