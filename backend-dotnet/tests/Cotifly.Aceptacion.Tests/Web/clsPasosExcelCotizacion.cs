using System.Net;
using ClosedXML.Excel;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/excel_cotizacion.feature</c>: <c>GET /web/cotizaciones/{folio}/excel</c> con el
/// dueño (versión interna), el gerente de su regional y el director (sin costos ni márgenes) y los ajenos (404). Lee el libro de la última
/// respuesta de <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosExcelCotizacion(clsContextoWeb web)
{
    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [When("{string} descarga el Excel de la cotización {string}")]
    public Task CuandoDescarga(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{Uri.EscapeDataString(web.Cotizacion(cotizacion).Folio)}/excel", web.Sesion(alias).Access);

    [Given("que la cotización {string} todavía no tiene precio")]
    public Task DadoSinPrecio(string cotizacion) =>
        clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET precioFinal = NULL WHERE id = '{web.Cotizacion(cotizacion).Id}'");

    [Then(@"^la respuesta es el Excel ""([^""]+)"" con las hojas ""([^""]+)"", ""([^""]+)"", ""([^""]+)"" y ""([^""]+)""$")]
    public void EntoncesElExcel(string nombre, string h1, string h2, string h3, string h4)
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        web.UltimoTipo.Should().Be(TipoXlsx);
        using var libro = Libro();
        libro.Worksheets.Select(h => h.Name).Should().Equal(h1, h2, h3, h4);
        var alias = nombre[..nombre.IndexOf('.', StringComparison.Ordinal)];
        nombre.Should().Be($"{alias}.xlsx");
        libro.Worksheet("Resumen").Cell(3, 2).GetString().Should().Be(web.Cotizacion(alias).Folio, "el resumen empieza por el folio");
    }

    [Then("el Excel es la versión {string}")]
    public void EntoncesLaVersion(string version)
    {
        using var libro = Libro();
        Dato(libro, "Versión del archivo").Should().Be(version);
    }

    [Then("la hoja {string} tiene las columnas {string} y {string}")]
    public void EntoncesLasColumnas(string hoja, string a, string b)
    {
        using var libro = Libro();
        Encabezados(libro.Worksheet(hoja)).Should().Contain([a, b]);
    }

    [Then("la ruta {int} del Excel tiene el costo bruto {long}, el margen de {int} % y el precio {long}")]
    public void EntoncesLaRutaInterna(int ruta, long costo, int porcentaje, long precio)
    {
        var margen = porcentaje / 100m;
        using var libro = Libro();
        var hoja = libro.Worksheet("Rutas");
        Valor(hoja, ruta, "Costo bruto (COP)").Should().Be(costo);
        Valor(hoja, ruta, "Margen aplicado").Should().Be(margen);
        Valor(hoja, ruta, "Precio ruta (COP)").Should().Be(precio);
    }

    [Then("la ruta {int} del Excel tiene el precio {long}")]
    public void EntoncesLaRuta(int ruta, long precio)
    {
        using var libro = Libro();
        Valor(libro.Worksheet("Rutas"), ruta, "Precio ruta (COP)").Should().Be(precio);
    }

    [Then("el camión {int} de la ruta {int} del Excel tiene el costo {long} y el precio {long}")]
    public void EntoncesElCamion(int camion, int ruta, long costo, long precio)
    {
        using var libro = Libro();
        var hoja = libro.Worksheet("Camiones");
        var columnas = Encabezados(hoja);
        var fila = hoja.RowsUsed().Skip(1).Single(f => f.Cell(1).GetValue<int>() == ruta && f.Cell(3).GetValue<int>() == camion);
        fila.Cell(columnas.IndexOf("Costo camión (COP)") + 1).GetValue<decimal>().Should().Be(costo);
        fila.Cell(columnas.IndexOf("Precio transporte (COP)") + 1).GetValue<decimal>().Should().Be(precio);
    }

    [Then("el resumen del Excel dice que el precio está congelado")]
    public void EntoncesCongelado()
    {
        using var libro = Libro();
        Dato(libro, "Precio congelado").Should().Be("Sí");
    }

    [Then("ninguna hoja del Excel tiene una columna de costo ni de margen")]
    public void EntoncesSinColumnasInternas()
    {
        using var libro = Libro();
        // Los nombres de las columnas (fila 1 de cada hoja de datos) y las etiquetas del resumen (columna 1).
        var textos = libro.Worksheets.Where(h => h.Name != "Resumen").SelectMany(Encabezados)
            .Concat(libro.Worksheet("Resumen").Column(1).CellsUsed().Select(c => c.GetString()))
            .ToList();
        textos.Should().NotBeEmpty();
        textos.Should().NotContain(t => t.Contains("Costo", StringComparison.OrdinalIgnoreCase) || t.Contains("Margen", StringComparison.OrdinalIgnoreCase),
            "el gerente y el director no reciben costos ni márgenes (con el margen y el precio se deduce el costo)");
    }

    [Then("el Excel no contiene el costo {long} ni el margen de {int} %")]
    public void EntoncesSinValoresInternos(long costo, int porcentaje)
    {
        var margen = porcentaje / 100m;
        using var libro = Libro();
        libro.Worksheets.SelectMany(h => h.CellsUsed()).Where(c => c.DataType == XLDataType.Number).Select(c => c.GetValue<decimal>())
            .Should().NotContain([costo, margen]);
    }

    private XLWorkbook Libro() => new(new MemoryStream(web.UltimoContenido));

    private static List<string> Encabezados(IXLWorksheet hoja) => [.. hoja.Row(1).CellsUsed().Select(c => c.GetString())];

    private static decimal Valor(IXLWorksheet hoja, int ruta, string columna)
    {
        var columnas = Encabezados(hoja);
        columnas.Should().Contain(columna);
        var fila = hoja.RowsUsed().Skip(1).First(f => f.Cell(1).DataType == XLDataType.Number && f.Cell(1).GetValue<int>() == ruta);
        return fila.Cell(columnas.IndexOf(columna) + 1).GetValue<decimal>();
    }

    private static string Dato(XLWorkbook libro, string etiqueta) =>
        libro.Worksheet("Resumen").RowsUsed().Single(f => f.Cell(1).GetString() == etiqueta).Cell(2).GetString();
}
