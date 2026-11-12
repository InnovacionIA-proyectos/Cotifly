using System.Diagnostics;
using System.IO.Compression;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Infraestructura.Cotizaciones.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Cotifly.Infraestructura.Tests.Cotizaciones;

/// <summary>
/// HU-018.1 (#155, parte C): el Excel de la cotización con ClosedXML. La versión del dueño trae las columnas de costo (COP) y de margen;
/// la del gerente y del director no trae ninguna (ni encabezados ni valores). El archivo es un <c>.xlsx</c> válido (abre sin reparaciones)
/// y 50 rutas con 3 camiones se generan en menos de 2 s.
/// </summary>
public sealed class clsExportadorExcelCotizacionTests
{
    private const decimal CostoFlete = 1_000_003m;
    private const decimal CostoConexo = 200_007m;
    private const decimal Margen = 0.1537m;

    private static readonly clsExportadorExcelCotizacionClosedXml _exportador = new();

    [Fact]
    public void ElDuenoRecibeLasCuatroHojasConLasColumnasDeCostoYMargen()
    {
        using var libro = Abrir(_exportador.Generar(Datos()));

        libro.Worksheets.Select(h => h.Name).Should().Equal("Resumen", "Rutas", "Camiones", "Conexos");
        Encabezados(libro.Worksheet("Rutas")).Should().Equal(
            "Ruta", "Origen", "Destino", "Estado", "Motivo de rechazo", "Peso (kg)", "Camiones",
            "Costo fletes (COP)", "Costo retorno (COP)", "Costo levante (COP)", "Costo conexos (COP)", "Costo bruto (COP)",
            "Margen aplicado", "Margen sugerido", "Origen del margen", "Método de cálculo",
            "Precio transporte (USD)", "Precio conexos (USD)", "Seguro de la mercancía (USD)", "Precio ruta (USD)");
        Encabezados(libro.Worksheet("Camiones")).Should().Contain(["Costo camión (COP)", "Margen", "Precio transporte (USD)"]);
        Encabezados(libro.Worksheet("Conexos")).Should().Equal("Ruta", "Origen → destino", "Servicio", "Categoría", "Cantidad", "Costo (COP)", "Precio (USD)");
        Numeros(libro).Should().Contain([CostoFlete, CostoConexo, Margen]);
        var rutas = libro.Worksheet("Rutas");
        rutas.Cell(2, 13).Style.NumberFormat.Format.Should().Be("0.00%");
        rutas.Cell(2, 20).Style.NumberFormat.Format.Should().Be("#,##0.00", "dólares con dos decimales");
        rutas.Cell(2, 8).Style.NumberFormat.Format.Should().Be("#,##0", "el costo siempre en pesos");
        rutas.Cell(3, 4).GetString().Should().Be("Rechazada en NEXO");
        rutas.Cell(3, 20).GetString().Should().Be(clsExportadorExcelCotizacionClosedXml.SinPrecio);
        rutas.Cell(2, 19).IsEmpty().Should().BeTrue("HU-045.3: sin seguro la celda queda vacía");
        rutas.Cell(4, 1).GetString().Should().Be("Total");
        rutas.Cell(4, 12).GetValue<decimal>().Should().Be(1_200_010m, "el costo bruto total va en la columna del costo bruto");
        Dato(libro, "Versión del archivo").Should().Be(clsExportadorExcelCotizacionClosedXml.VersionInterna);
        DatoNumero(libro, "Costo bruto total (COP)").Should().Be(1_200_010m);
        DatoNumero(libro, "TRM aplicada").Should().Be(3917.37m);
    }

    [Fact]
    public void ElLectorQueNoEsDuenoNoRecibeNingunaColumnaNiValorDeCostoNiDeMargen()
    {
        var bytes = _exportador.Generar(Datos().SinCostos());
        using var libro = Abrir(bytes);

        var etiquetas = libro.Worksheets.Where(h => h.Name != "Resumen").SelectMany(Encabezados)
            .Concat(libro.Worksheet("Resumen").Column(1).CellsUsed().Select(c => c.GetString()))
            .ToList();
        etiquetas.Should().NotContain(t => t.Contains("Costo", StringComparison.OrdinalIgnoreCase) || t.Contains("Margen", StringComparison.OrdinalIgnoreCase));
        Numeros(libro).Should().NotContain([CostoFlete, CostoConexo, Margen, 1_200_010m]);
        Encabezados(libro.Worksheet("Rutas")).Should().Equal(
            "Ruta", "Origen", "Destino", "Estado", "Motivo de rechazo", "Peso (kg)", "Camiones",
            "Precio transporte (USD)", "Precio conexos (USD)", "Seguro de la mercancía (USD)", "Precio ruta (USD)");
        libro.Worksheet("Rutas").Cell(2, 11).GetValue<decimal>().Should().Be(310.25m, "los precios se conservan");
        Dato(libro, "Versión del archivo").Should().Be(clsExportadorExcelCotizacionClosedXml.VersionSoloLectura);

        // Ninguna parte del paquete (ni cadenas compartidas ni estilos) trae los costos ni el margen.
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var entrada in zip.Entries)
        {
            using var lector = new StreamReader(entrada.Open());
            var texto = lector.ReadToEnd();
            texto.Should().NotContain("1000003", entrada.FullName).And.NotContain("200007", entrada.FullName).And.NotContain("0.1537", entrada.FullName);
        }
    }

    [Fact]
    public void ElArchivoEsUnXlsxValidoQueAbreSinReparaciones()
    {
        foreach (var datos in new[] { Datos(), Datos().SinCostos() })
        {
            using var documento = SpreadsheetDocument.Open(new MemoryStream(_exportador.Generar(datos)), false);
            new OpenXmlValidator().Validate(documento, TestContext.Current.CancellationToken).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Validación HU-018.1 (#155): en la versión del gerente y el director no hay dónde esconder un costo o un margen: cada número del
    /// libro es uno de los que ven todos (orden, pesos, camiones, cantidades, TRM y precios), y el paquete no trae fórmulas, comentarios,
    /// nombres definidos, filas, columnas u hojas ocultas, ni el origen o el método del margen, ni el color de las columnas internas.
    /// </summary>
    [Fact]
    public void LaVersionSinCostosNoEscondeNadaInternoEnNingunaParteDelPaquete()
    {
        var bytes = _exportador.Generar(Datos().SinCostos());

        using (var libro = Abrir(bytes))
        {
            decimal[] visibles = [1m, 2m, 0m, 15_000m, 10_000m, 5000m, 255.29m, 54.96m, 310.25m, 3917.37m];
            Numeros(libro).Should().OnlyContain(n => visibles.Contains(n), "ningún número fuera de los que ven todos");
            libro.Worksheets.Select(h => h.Name).Should().Equal("Resumen", "Rutas", "Camiones", "Conexos");
            libro.Worksheets.Should().OnlyContain(h => h.Visibility == XLWorksheetVisibility.Visible);
            libro.DefinedNames.Should().BeEmpty();
            foreach (var hoja in libro.Worksheets)
            {
                hoja.CellsUsed().Should().OnlyContain(c => !c.HasFormula && !c.HasComment, hoja.Name);
                hoja.ColumnsUsed().Should().OnlyContain(c => !c.IsHidden, hoja.Name);
                hoja.RowsUsed().Should().OnlyContain(f => !f.IsHidden, hoja.Name);
            }
        }

        using var zip = new ZipArchive(new MemoryStream(bytes));
        zip.Entries.Select(e => e.FullName).Should().NotContain(n => n.Contains("comment", StringComparison.OrdinalIgnoreCase));
        foreach (var entrada in zip.Entries)
        {
            using var lector = new StreamReader(entrada.Open());
            var texto = lector.ReadToEnd();
            texto.Should().NotContain("politica_linea", entrada.FullName).And.NotContain(">margen<", entrada.FullName)
                .And.NotContain("FCE4D6", entrada.FullName, "el color de las columnas internas no se usa")
                .And.NotContain("<f>", entrada.FullName).And.NotContain("Costo", entrada.FullName);
        }
    }

    [Fact]
    public void CincuentaRutasConTresCamionesSeGeneranEnMenosDeDosSegundos()
    {
        var datos = Datos() with { Rutas = [.. Enumerable.Range(1, 50).Select(i => Ruta(i, camiones: 3))] };
        _exportador.Generar(datos); // calentamiento (carga de ClosedXML y de las fuentes)

        // El mejor de 3 intentos: la suite corre en paralelo (y a veces junto a otra suite), así que un solo intento mide la carga de la
        // máquina y no el exportador.
        byte[] bytes = [];
        var mejor = TimeSpan.MaxValue;
        for (var intento = 0; intento < 3; intento++)
        {
            var reloj = Stopwatch.StartNew();
            bytes = _exportador.Generar(datos);
            reloj.Stop();
            mejor = reloj.Elapsed < mejor ? reloj.Elapsed : mejor;
        }

        mejor.Should().BeLessThan(TimeSpan.FromSeconds(2));
        using var libro = Abrir(bytes);
        libro.Worksheet("Camiones").RowsUsed().Count().Should().Be(151, "encabezado + 50 × 3 camiones");
    }

    private static clsExcelCotizacion Datos() => new(
        "PCN-AP-27102026-001", "LONGYIN S.A.S.", "900123456", "Ana Pérez", "licitacion", "USD", 3917.37m, "entregada", null,
        new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero), true, new DateTimeOffset(2026, 10, 27, 16, 0, 0, TimeSpan.Zero), false,
        [
            Ruta(1, camiones: 1),
            new clsExcelRuta(2, "Buenaventura", "Cali", "Rechazada en NEXO", true, "Sin operación", 5000m, 0, null, null, null,
                [new clsExcelCamion(1, "3S3", "FURGON", 5000m, "Rechazada en NEXO", false, null, new clsExcelInternoCamion(null, null, null, null, null))],
                [],
                new clsExcelInternoRuta(null, null, null, null, null, null, 0.15m, "politica_linea", "margen")),
        ],
        new clsExcelTotales(2, 1, 1, 15_000m, 255.29m, 54.96m, 310.25m, new clsExcelInternoTotales(1_200_010m)));

    private static clsExcelRuta Ruta(int orden, int camiones) => new(
        orden, "Buenaventura", "Bogotá, D.C.", "Costeada", false, null, 10_000m, camiones, 255.29m, 54.96m, 310.25m,
        [.. Enumerable.Range(1, camiones).Select(n => new clsExcelCamion(n, "3S3", "FURGON", 10_000m, "Costeado", true, 255.29m,
            new clsExcelInternoCamion(CostoFlete, 0m, 0m, CostoFlete, Margen)))],
        [new clsExcelConexo("Cargue", "logistico", 2m, 54.96m, new clsExcelInternoConexo(CostoConexo))],
        new clsExcelInternoRuta(CostoFlete, 0m, 0m, CostoConexo, 1_200_010m, Margen, 0.15m, "politica_linea", "margen"));

    private static XLWorkbook Abrir(byte[] bytes) => new(new MemoryStream(bytes));

    private static List<string> Encabezados(IXLWorksheet hoja) => [.. hoja.Row(1).CellsUsed().Select(c => c.GetString())];

    private static List<decimal> Numeros(XLWorkbook libro) =>
        [.. libro.Worksheets.SelectMany(h => h.CellsUsed()).Where(c => c.DataType == XLDataType.Number).Select(c => c.GetValue<decimal>())];

    private static IXLCell Celda(XLWorkbook libro, string etiqueta) =>
        libro.Worksheet("Resumen").RowsUsed().Single(f => f.Cell(1).GetString() == etiqueta).Cell(2);

    private static string Dato(XLWorkbook libro, string etiqueta) => Celda(libro, etiqueta).GetString();

    private static decimal DatoNumero(XLWorkbook libro, string etiqueta) => Celda(libro, etiqueta).GetValue<decimal>();
}
