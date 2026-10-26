using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/mercancias_excel.feature</c> (HU-035.1, #202, ADR-024): el Excel de mercancías de una ruta leído en
/// Cotifly sin Nexo. Reutiliza el Nexo falso de <see cref="clsPasosCosteo"/> y el cubicador falso de <see cref="clsPasosCubicador"/>;
/// los archivos salen de <see cref="clsExcelMercanciasMuestra"/>.
/// </summary>
[Binding]
public sealed class clsPasosExcelMercancias(clsContextoWeb web, clsPasosCosteo costeo, clsPasosCubicador cubicador)
{
    private const string Ruta = "/web/mercancias/analizar-excel";

    [Given("que el cubicador tiene el tipo de mercancía {string} con el código {string}")]
    public void DadoElTipo(string nombre, string codigo) => cubicador.Cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso(codigo, nombre));

    [When("{string} analiza el Excel de mercancías de {int} filas")]
    public Task CuandoAnaliza(string alias, int filas) => AnalizarAsync(alias, clsExcelMercanciasMuestra.Excel(filas), "mercancias.xlsx");

    [When("{string} analiza un Excel de mercancías con una fórmula y una fecha")]
    public Task CuandoAnalizaConFormula(string alias) => AnalizarAsync(alias, clsExcelMercanciasMuestra.ConFormulaYFecha(), "mercancias.xlsx");

    [When(@"^""([^""]+)"" analiza como Excel de mercancías (.+)$")]
    public Task CuandoAnalizaArchivo(string alias, string archivo) => archivo switch
    {
        "un archivo de texto renombrado a .xlsx" => AnalizarAsync(alias, clsExcelMercanciasMuestra.TextoRenombrado(), "mercancias.xlsx"),
        "un zip que no es un libro de Excel" => AnalizarAsync(alias, clsExcelMercanciasMuestra.ZipSinLibro(), "mercancias.xlsx"),
        "una bomba zip" => AnalizarAsync(alias, clsExcelMercanciasMuestra.BombaZip(), "mercancias.xlsx"),
        "un libro pequeño con millones de celdas" => AnalizarAsync(alias, clsExcelMercanciasMuestra.Densa(4_000_000), "mercancias.xlsx"),
        "un libro con una entidad externa (XXE)" => AnalizarAsync(alias, clsExcelMercanciasMuestra.ConEntidadExterna(), "mercancias.xlsx"),
        "un archivo .csv" => AnalizarAsync(alias, clsExcelMercanciasMuestra.TextoRenombrado(), "mercancias.csv"),
        "un libro sin datos" => AnalizarAsync(alias, clsExcelMercanciasMuestra.SinDatos(), "mercancias.xlsx"),
        _ => throw new ArgumentOutOfRangeException(nameof(archivo), archivo, "Archivo de prueba desconocido."),
    };

    [When("{string} envía JSON al análisis del Excel de mercancías")]
    public Task CuandoEnviaJson(string alias) => web.EnviarAsync(HttpMethod.Post, Ruta, web.Sesion(alias).Access, """{"archivo":"mercancias.xlsx"}""");

    [When("se analiza el Excel de mercancías sin sesión")]
    public Task CuandoSinSesion() => web.EnviarAsync(HttpMethod.Post, Ruta, null, clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(1), "mercancias.xlsx"));

    [Then("la hoja {string} trae los encabezados {string}")]
    public void EntoncesEncabezados(string hoja, string encabezados) =>
        Textos(Hoja(hoja).GetProperty("encabezados")).Should().Equal(encabezados.Split('|'));

    [Then("la hoja {string} trae {int} filas desde la fila {int} de Excel")]
    public void EntoncesFilas(string hoja, int filas, int desde)
    {
        var json = Hoja(hoja);
        json.GetProperty("filas").GetArrayLength().Should().Be(filas);
        json.GetProperty("filas_excel").EnumerateArray().Select(f => f.GetInt32()).Should().Equal(Enumerable.Range(desde, filas));
        json.GetProperty("filas_truncadas").GetBoolean().Should().BeFalse();
    }

    [Then("la hoja {string} trae {int} filas de un total de {int} y marca las filas truncadas")]
    public void EntoncesTruncada(string hoja, int filas, int total)
    {
        var json = Hoja(hoja);
        json.GetProperty("filas").GetArrayLength().Should().Be(filas);
        json.GetProperty("total_filas").GetInt32().Should().Be(total);
        json.GetProperty("filas_truncadas").GetBoolean().Should().BeTrue();
    }

    [Then("la fila {int} de la hoja {string} es {string}")]
    public void EntoncesLaFila(int fila, string hoja, string valores) =>
        Textos(Hoja(hoja).GetProperty("filas")[fila]).Should().Equal(valores.Split('|'));

    [Then("la fila {int} de la hoja {string} sugiere el código {string}")]
    public void EntoncesSugiere(int fila, string hoja, string codigo)
    {
        var mercancia = Hoja(hoja).GetProperty("mercancias").EnumerateArray().Single(m => m.GetProperty("fila").GetInt32() == fila);
        mercancia.GetProperty("codigo_mercancia").GetString().Should().Be(codigo);
        mercancia.GetProperty("sugerido").GetBoolean().Should().BeTrue();
        web.UltimoJson().GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeTrue();
    }

    [Then("el análisis no trae el catálogo de mercancías")]
    public void EntoncesSinCatalogo() => web.UltimoJson().GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeFalse();

    [Then("Nexo no recibió el archivo")]
    public void EntoncesNexoNoRecibio()
    {
        costeo.Nexo.UltimoArchivoAnalizado.Should().BeNull();
        costeo.Nexo.Peticiones.Should().NotContain(p => p.Metodo == HttpMethod.Post, "el Excel de mercancías nunca sale a Nexo");
    }

    private Task AnalizarAsync(string alias, byte[] archivo, string nombre) =>
        web.EnviarAsync(HttpMethod.Post, Ruta, web.Sesion(alias).Access, clsApiWebPruebas.Archivo(archivo, nombre));

    private JsonElement Hoja(string nombre) =>
        web.UltimoJson().GetProperty("hojas").EnumerateArray().Single(h => h.GetProperty("nombre").GetString() == nombre);

    private static List<string?> Textos(JsonElement arreglo) => [.. arreglo.EnumerateArray().Select(e => e.GetString())];
}
