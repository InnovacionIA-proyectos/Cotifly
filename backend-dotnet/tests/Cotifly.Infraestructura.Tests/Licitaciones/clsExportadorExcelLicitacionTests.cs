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
    private static clsExportacionLicitacion Datos(byte[]? original, string moneda = "COP", decimal trm = 0m, decimal? total = null, bool conexos = true) => new(
        "PCN-900555111-DG-01102026-001", "Agencia JF", "900555111", moneda, trm, total, clsPliegoMuestra.Hoja, original,
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

        var exportado = clsExportadorExcelLicitacionClosedXml.Generar(Datos(original));

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
    public void Hu183_UnaLineaRechazadaSaleSinCosteoNoEsPendienteYElTotalSeMuestraSinElla()
    {
        // HU-028.1 (#183): "SIN COSTEO" en el precio, "Rechazada en NEXO" en el estado (sin motivo), contada aparte en el resumen y marcada en
        // la hoja de camiones; el total (sin ella) se muestra porque no queda ninguna pendiente.
        var datos = Datos(clsPliegoMuestra.Excel(3), total: 1_176_471m) with
        {
            Lineas =
            [
                new clsLineaExportacion(1, 0, "BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32000m, "Costeada", 1_176_471m,
                    [new clsCamionExportacion(1, "3S3", null, 32000m, null, null, null)]),
                new clsLineaExportacion(2, 2, "CUCUTA", "BOGOTA, D.C.", "2", 8000m, "Rechazada en NEXO", null,
                    [new clsCamionExportacion(1, "2", null, 8000m, null, null, null) { Rechazado = true }]) { Rechazada = true },
            ],
        };

        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));

        var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(clsPliegoMuestra.FilaExcel(2), 7).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.SinCosteo);
        hoja.Cell(clsPliegoMuestra.FilaExcel(2), 8).GetString().Should().Be("Rechazada en NEXO");
        var resumen = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen);
        var textos = resumen.CellsUsed().Select(c => c.GetFormattedString()).ToList();
        textos.Should().Contain(clsExportadorExcelLicitacionClosedXml.EtiquetaRechazadas);
        textos.Should().NotContain(t => t.StartsWith("Pendiente:", StringComparison.Ordinal), "una rechazada no es pendiente");
        resumen.CellsUsed().Should().Contain(c => c.DataType == XLDataType.Number && c.GetValue<decimal>() == 1_176_471m, "el total sin la rechazada");
        var camiones = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaCamiones);
        camiones.Cell(1, 11).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ColumnaCosteoCamion);
        camiones.Column(11).CellsUsed().Select(c => c.GetString()).Should().Contain(clsCamionExportacion.TextoRechazado);
    }

    [Fact]
    public void Hu183_ConTodasLasLineasRechazadasElTotalDiceSinCosteo()
    {
        var datos = Datos(null, total: null) with
        {
            Lineas = [new clsLineaExportacion(1, 0, "A", "B", "3S3", 1m, "Rechazada en NEXO", null) { Rechazada = true }],
        };

        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));

        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetFormattedString())
            .Should().Contain(clsExportadorExcelLicitacionClosedXml.TodasRechazadas);
    }

    [Fact]
    public void ElResumenTraeElTotalSoloSiTodasLasLineasTienenPrecioYLosConexosConSuPrecio()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel(3), total: 9m)).Valor));
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

        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));

        var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(3, 7).GetString().Should().Be("Precio final Cotifly (USD)");
        hoja.Cell(clsPliegoMuestra.FilaExcel(0), 7).Style.NumberFormat.Format.Should().Be("#,##0.00");
        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetString()).Should().Contain("TRM aplicada");
    }

    [Fact]
    public void SinOriginalArmaUnaHojaConLosCamposMapeados()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(null, conexos: false)).Valor));

        var hoja = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaSinOriginal);
        hoja.Cell(1, 2).GetString().Should().Be("Origen");
        hoja.Cell(2, 2).GetString().Should().Be("BUENAVENTURA");
        hoja.Cell(2, 6).GetValue<decimal>().Should().Be(1_176_471m);
        hoja.Cell(3, 6).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.SinCosteo);
        hoja.Cell(2, 8).GetString().Should().BeEmpty("sin conexos incluidos no hay nada que remitir al resumen");
    }

    // HU-044.10 (#293, parte 2): con analizar_excel y original, las líneas a mano (fila null) van a una hoja nueva «Agregadas en Cotifly»
    // (columnas de «Licitación» + «Referencia» = «Agregada en Cotifly»); «Resumen Cotifly» agrega la fila «Líneas agregadas en Cotifly».
    [Fact]
    public void Hu293_ConOriginalLasLineasAManoVanAUnaHojaAgregadasConReferencia()
    {
        var datos = Datos(clsPliegoMuestra.Excel(3)) with
        {
            Lineas =
            [
                new clsLineaExportacion(1, 0, "BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32000m, "Costeada", 1_176_471m),
                new clsLineaExportacion(2, null, "CALI", "MEDELLIN", "2", 9000m, "Pendiente de costeo", null),
            ],
        };

        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));

        libro.Worksheets.Select(h => h.Name).Should().Contain(clsExportadorExcelLicitacionClosedXml.HojaAgregadas);
        var agregadas = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaAgregadas);
        agregadas.Cell(2, 2).GetString().Should().Be("CALI", "la línea a mano va a la hoja nueva");
        agregadas.Cell(1, 10).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ColumnaReferencia);
        agregadas.Cell(2, 10).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ReferenciaAgregada);
        // La línea con fila (0) sigue sobre el pliego, no en la hoja nueva.
        libro.Worksheet(clsPliegoMuestra.Hoja).Cell(clsPliegoMuestra.FilaExcel(0), 7).GetValue<decimal>().Should().Be(1_176_471m);
        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetFormattedString())
            .Should().Contain(clsExportadorExcelLicitacionClosedXml.EtiquetaAgregadas);
    }

    // HU-044.10 (#293, parte 2): con leer_estructura (o sin original), las líneas a mano van al final de la hoja «Licitación», con la
    // columna «Referencia» (la 10, solo si hay alguna a mano).
    [Fact]
    public void Hu293_LeidaConIaLasLineasAManoVanAlFinalDeLicitacionConReferencia()
    {
        var datos = Datos(null) with
        {
            LeidaConIa = true,
            Lineas =
            [
                new clsLineaExportacion(1, 0, "A", "B", "3S3", 10000m, "Costeada", 100m),
                new clsLineaExportacion(2, null, "C", "D", "2", 9000m, "Pendiente de costeo", null),
            ],
        };

        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(datos).Valor));

        libro.Worksheets.Select(h => h.Name).Should().NotContain(clsExportadorExcelLicitacionClosedXml.HojaAgregadas);
        var hoja = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaSinOriginal);
        hoja.Cell(1, 10).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ColumnaReferencia);
        hoja.Cell(3, 2).GetString().Should().Be("C", "la línea a mano queda al final");
        hoja.Cell(3, 10).GetString().Should().Be(clsExportadorExcelLicitacionClosedXml.ReferenciaAgregada);
        hoja.Cell(2, 10).GetString().Should().BeEmpty("la línea con fila no se marca");
        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetFormattedString())
            .Should().Contain(clsExportadorExcelLicitacionClosedXml.EtiquetaAgregadas);
    }

    // HU-044.10 (#293, parte 2): sin líneas a mano, el Excel queda igual que hoy (sin hoja «Agregadas», sin columna «Referencia» ni la fila del resumen).
    [Fact]
    public void Hu293_SinLineasAManoElExcelQuedaIgualQueHoy()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel(3))).Valor));

        libro.Worksheets.Select(h => h.Name).Should().NotContain(clsExportadorExcelLicitacionClosedXml.HojaAgregadas);
        libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen).CellsUsed().Select(c => c.GetFormattedString())
            .Should().NotContain(clsExportadorExcelLicitacionClosedXml.EtiquetaAgregadas);
    }

    [Fact]
    public void UnOriginalIlegibleOSinLaHojaEsUnErrorDeNegocio()
    {
        clsExportadorExcelLicitacionClosedXml.Generar(Datos([1, 2, 3])).Error.Codigo.Should().Be(clsCodigosError.ExcelInvalido);
        clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel()) with { Hoja = "Otra" }).Error.Codigo.Should().Be(clsCodigosError.ExcelSinDatos);
    }
}
