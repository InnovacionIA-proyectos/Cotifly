using ClosedXML.Excel;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Exportación del Excel de la licitación (HU-011.3, ADR-017 §5): el original del cliente intacto, tres columnas nuevas a la
/// derecha, las líneas sin costeo marcadas y una hoja de resumen; sin original, una hoja con los campos mapeados.
/// </summary>
public sealed class clsExportadorExcelLicitacionTests
{
    private readonly clsExportadorExcelLicitacionClosedXml _exportador = new();

    private static clsExportacionLicitacion Datos(byte[]? original, string moneda = "COP", decimal trm = 0m, decimal? total = null, bool conexos = true) => new(
        "PCN-900555111-DG-01102026-001", "Agencia JF", "900555111", "Pliego 2026-04", moneda, trm, total, clsPliegoMuestra.Hoja, original,
        [
            new clsLineaExportacion(1, 0, "BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32000m, "Costeada", 1_176_471m),
            new clsLineaExportacion(2, 2, "CUCUTA", "BOGOTA, D.C.", "2", 8000m, "Pendiente de costeo", null),
        ],
        conexos ? [new clsConexoExportacion("Cargue", "logistico", 1m, 200_000m)] : [],
        new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ConElOriginalLasColumnasDelClienteQuedanIntactasYLasNuevasVanALaDerecha()
    {
        var original = clsPliegoMuestra.Excel(3);

        var exportado = _exportador.Generar(Datos(original));

        exportado.EsExitoso.Should().BeTrue();
        using var antes = new XLWorkbook(new MemoryStream(original));
        using var despues = new XLWorkbook(new MemoryStream(exportado.Valor));
        var hojaAntes = antes.Worksheet(clsPliegoMuestra.Hoja);
        var hojaDespues = despues.Worksheet(clsPliegoMuestra.Hoja);
        foreach (var celda in hojaAntes.RangeUsed()!.Cells())
        {
            hojaDespues.Cell(celda.Address.RowNumber, celda.Address.ColumnNumber).GetFormattedString()
                .Should().Be(celda.GetFormattedString(), $"la celda {celda.Address} del cliente no se toca");
        }

        hojaDespues.Cell(3, 7).GetString().Should().Be("Precio final Cotifly (COP)");
        hojaDespues.Cell(3, 8).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ColumnaEstado);
        hojaDespues.Cell(3, 9).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ColumnaConexos);
        hojaDespues.Cell(clsPliegoMuestra.FilaExcel(0), 7).GetValue<decimal>().Should().Be(1_176_471m);
        hojaDespues.Cell(clsPliegoMuestra.FilaExcel(0), 7).Style.NumberFormat.Format.Should().Be("#,##0");
        hojaDespues.Cell(clsPliegoMuestra.FilaExcel(0), 9).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ConexosDeLaLicitacion);
        hojaDespues.Cell(clsPliegoMuestra.FilaExcel(1), 8).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.SinEnviar, "la fila 1 del pliego no se envió");
        var pendiente = hojaDespues.Cell(clsPliegoMuestra.FilaExcel(2), 7);
        pendiente.GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.SinCosteo);
        pendiente.Style.Fill.BackgroundColor.Should().Be(XLColor.FromHtml("#FFF2CC"));
        hojaDespues.Row(6).CellsUsed().Should().BeEmpty("la fila vacía del pliego sigue vacía");
        despues.Worksheets.Select(h => h.Name).Should().Contain(["Instrucciones", clsExportadorExcelLicitacionClosedXml.HojaResumen]);
    }

    [Fact]
    public void ElResumenTraeElTotalSoloSiTodasLasLineasTienenPrecioYLosConexosConSuPrecio()
    {
        using var libro = new XLWorkbook(new MemoryStream(_exportador.Generar(Datos(clsPliegoMuestra.Excel(3), total: 9m)).Valor));
        var resumen = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen);
        var textos = resumen.CellsUsed().Select(c => c.GetFormattedString()).ToList();

        textos.Should().Contain("Pendiente: 1 línea(s) sin costeo");
        textos.Should().Contain("Cargue");
        resumen.CellsUsed().Should().Contain(c => c.DataType == XLDataType.Number && c.GetValue<decimal>() == 200_000m, "el conexo va con su precio");
        textos.Should().NotContain("9", "con líneas pendientes el total no se muestra");
    }

    [Fact]
    public void EnDolaresLaColumnaLlevaLaMonedaDosDecimalesYLaTrm()
    {
        var datos = Datos(clsPliegoMuestra.Excel(3), moneda: "USD", trm: 3_950.25m) with
        {
            Lineas = [new clsLineaExportacion(1, 0, "A", "B", null, null, "Costeada", 297.84m)],
            PrecioTotal = 297.84m,
        };

        using var libro = new XLWorkbook(new MemoryStream(_exportador.Generar(datos).Valor));

        var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(3, 7).GetString().Should().Be("Precio final Cotifly (USD)");
        hoja.Cell(clsPliegoMuestra.FilaExcel(0), 7).Style.NumberFormat.Format.Should().Be("#,##0.00");
        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetString()).Should().Contain("TRM aplicada");
    }

    [Fact]
    public void SinOriginalArmaUnaHojaConLosCamposMapeados()
    {
        using var libro = new XLWorkbook(new MemoryStream(_exportador.Generar(Datos(null, conexos: false)).Valor));

        var hoja = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaSinOriginal);
        hoja.Cell(1, 2).GetString().Should().Be("Origen");
        hoja.Cell(2, 2).GetString().Should().Be("BUENAVENTURA");
        hoja.Cell(2, 6).GetValue<decimal>().Should().Be(1_176_471m);
        hoja.Cell(3, 6).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.SinCosteo);
        hoja.Cell(2, 8).GetString().Should().BeEmpty("sin conexos incluidos no hay nada que remitir al resumen");
    }

    [Fact]
    public void UnOriginalIlegibleOSinLaHojaEsUnErrorDeNegocio()
    {
        _exportador.Generar(Datos([1, 2, 3])).Error.Codigo.Should().Be(clsCodigosError.ExcelInvalido);
        _exportador.Generar(Datos(clsPliegoMuestra.Excel()) with { Hoja = "Otra" }).Error.Codigo.Should().Be(clsCodigosError.ExcelSinDatos);
    }
}
