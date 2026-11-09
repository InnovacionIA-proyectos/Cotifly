using ClosedXML.Excel;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9): el Excel de la licitación agrega la columna del seguro de la mercancía (después de las cuatro de Cotifly) solo
/// si alguna línea lo incluye, y su suma en la hoja de resumen, aparte del total. Sin seguro (hoy, porque las licitaciones no lo admiten) el
/// archivo no cambia.
/// </summary>
public sealed class clsExportadorExcelLicitacionSeguroTests
{
    private static clsExportacionLicitacion Datos(byte[]? original, decimal? seguro) => new(
        "PCN-900555111-DG-05112026-001", "Agencia JF", "900555111", "COP", 0m, 2_176_471m, clsPliegoMuestra.Hoja, original,
        [
            new clsLineaExportacion(1, 0, "BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32000m, "Costeada", 1_176_471m) { ValorSeguro = seguro },
            new clsLineaExportacion(2, 2, "CUCUTA", "BOGOTA, D.C.", "2", 8000m, "Costeada", 1_000_000m),
        ],
        [],
        new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ConSeguroLaLineaLoTraeEnLaQuintaColumnaYElResumenSuSuma()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel(3), 690_000m)).Valor));

        var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(3, 11).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ColumnaSeguro("COP"));
        hoja.Cell(clsPliegoMuestra.FilaExcel(0), 11).GetValue<decimal>().Should().Be(690_000m);
        hoja.Cell(clsPliegoMuestra.FilaExcel(0), 11).Style.NumberFormat.Format.Should().Be("#,##0");
        hoja.Cell(clsPliegoMuestra.FilaExcel(2), 11).IsEmpty().Should().BeTrue("la línea 2 no incluye el seguro");
        var resumen = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).RowsUsed()
            .Single(f => f.Cell(1).GetString() == clsExportadorExcelLicitacionClosedXml.EtiquetaTotalSeguro).Cell(2);
        resumen.GetValue<decimal>().Should().Be(690_000m);
    }

    [Fact]
    public void SinSeguroNoHayQuintaColumnaNiDatoEnElResumen()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(null, null)).Valor));

        var hoja = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaSinOriginal);
        hoja.Row(1).CellsUsed().Select(c => c.GetString()).Should().NotContain(clsExportadorExcelLicitacionClosedXml.ColumnaSeguro("COP"));
        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetString())
            .Should().NotContain(clsExportadorExcelLicitacionClosedXml.EtiquetaTotalSeguro);
    }
}
