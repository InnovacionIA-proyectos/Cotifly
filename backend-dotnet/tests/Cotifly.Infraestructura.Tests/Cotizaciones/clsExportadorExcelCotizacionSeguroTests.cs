using ClosedXML.Excel;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Infraestructura.Cotizaciones.Services;

namespace Cotifly.Infraestructura.Tests.Cotizaciones;

/// <summary>
/// HU-045.3 (#265, ADR-032 §9): el Excel de la cotización trae el seguro de la mercancía de cada ruta en su columna (aparte del transporte y
/// de los conexos), su total en la fila de totales y en el Resumen, y lo ven también el gerente y el director (es venta, no costo). La ruta
/// con el seguro marcado sin valor de la mercancía lo dice. Nunca la tasa.
/// </summary>
public sealed class clsExportadorExcelCotizacionSeguroTests
{
    private static readonly clsExportadorExcelCotizacionClosedXml _exportador = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaRutaTraeElSeguroEnSuColumnaYLosTotalesLoSuman(bool soloLectura)
    {
        var datos = soloLectura ? Datos().SinCostos() : Datos();

        using var libro = new XLWorkbook(new MemoryStream(_exportador.Generar(datos)));

        var rutas = libro.Worksheet(clsExportadorExcelCotizacionClosedXml.HojaRutas);
        var encabezados = rutas.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var columna = encabezados.IndexOf("Seguro de la mercancía (COP)") + 1;
        columna.Should().BeGreaterThan(0);
        encabezados[columna].Should().Be("Precio ruta (COP)", "el seguro va antes del precio de la ruta, que lo incluye");
        rutas.Cell(2, columna).GetValue<decimal>().Should().Be(690_000m);
        rutas.Cell(3, columna).GetString().Should().Be(clsExportadorExcelCotizacionClosedXml.SeguroSinValorMercancia);
        rutas.Cell(4, 1).GetString().Should().Be("Total");
        rutas.Cell(4, columna).GetValue<decimal>().Should().Be(690_000m);
        var resumen = libro.Worksheet(clsExportadorExcelCotizacionClosedXml.HojaResumen).RowsUsed()
            .Single(f => f.Cell(1).GetString() == "Seguro de la mercancía (COP)").Cell(2);
        resumen.GetValue<decimal>().Should().Be(690_000m);
        libro.Worksheets.SelectMany(h => h.CellsUsed()).Select(c => c.GetString())
            .Should().NotContain(t => t.Contains("tasa", StringComparison.OrdinalIgnoreCase) || t.Contains("0,00345", StringComparison.Ordinal)
                                      || t.Contains("0.00345", StringComparison.Ordinal));
    }

    /// <summary>
    /// B1 (decisión del usuario): en COP, una celda con centavos (el seguro, el precio de la ruta y los totales que lo incluyen) se formatea con
    /// 2 decimales para que lo que se ve cuadre con lo guardado; sin centavos sigue en pesos enteros.
    /// </summary>
    [Fact]
    public void EnPesosLasCeldasConCentavosMuestranLosDecimales()
    {
        var datos = Datos() with
        {
            Rutas = [Ruta(1, 1_250_000m) with { ValorSeguro = 100.40m, PrecioRuta = 1_250_100.40m }],
            Totales = Datos().Totales with { PrecioFinal = 1_250_100.40m, PrecioSeguro = 100.40m },
        };

        using var libro = new XLWorkbook(new MemoryStream(_exportador.Generar(datos)));

        var rutas = libro.Worksheet(clsExportadorExcelCotizacionClosedXml.HojaRutas);
        var encabezados = rutas.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var seguro = encabezados.IndexOf("Seguro de la mercancía (COP)") + 1;
        rutas.Cell(2, seguro).Style.NumberFormat.Format.Should().Be("#,##0.00");
        rutas.Cell(2, seguro + 1).Style.NumberFormat.Format.Should().Be("#,##0.00", "el precio de la ruta incluye los centavos del seguro");
        rutas.Cell(2, seguro - 2).Style.NumberFormat.Format.Should().Be("#,##0", "el transporte no tiene centavos");
        rutas.Cell(3, seguro + 1).Style.NumberFormat.Format.Should().Be("#,##0.00", "el total también");
        libro.Worksheet(clsExportadorExcelCotizacionClosedXml.HojaResumen).RowsUsed()
            .Single(f => f.Cell(1).GetString() == "Precio final (COP)").Cell(2).Style.NumberFormat.Format.Should().Be("#,##0.00");
    }

    /// <summary>Ruta 1 con seguro (1.250.000 de transporte + 690.000) y ruta 2 con el seguro marcado sin valor de la mercancía (1.000.000).</summary>
    private static clsExcelCotizacion Datos() => new(
        "PCN-AB-05112026-001", "Cliente", "900111222", "Ana", "spot", "COP", 0m, "autocosteo", null, null, false, DateTimeOffset.UnixEpoch, false,
        [
            Ruta(1, 1_250_000m) with { ValorSeguro = 690_000m },
            Ruta(2, 1_000_000m) with { SeguroSinValor = true },
        ],
        new clsExcelTotales(2, 2, 0, 20_000m, 2_250_000m, 0m, 2_940_000m, new clsExcelInternoTotales(1_800_000m)) { PrecioSeguro = 690_000m });

    private static clsExcelRuta Ruta(int orden, decimal transporte) => new(
        orden, "Buenaventura", "Bogotá, D.C.", "Costeada", false, null, 10_000m, 1, transporte, 0m, transporte,
        [new clsExcelCamion(1, "3S3", "FURGON", 10_000m, "Costeado", true, transporte, new clsExcelInternoCamion(900_000m, 0m, 0m, 900_000m, 0.2m))],
        [],
        new clsExcelInternoRuta(900_000m, 0m, 0m, 0m, 900_000m, 0.2m, 0.2m, "politica_linea", "margen"));
}
