using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/moneda_formulario_costeo.feature</c> y del escenario en USD de <c>ajuste_margen_costeo.feature</c>
/// (ADR-014 §5): la API real en memoria, SQL Server 2019 en Testcontainers, el Nexo falso y las fuentes de la TRM falsas
/// de <see cref="clsPasosCosteo"/> (DolarApi principal y Superfinanciera de respaldo). Los montos en dólares se escriben con punto decimal y
/// se comparan con la cultura invariante. El envío en pesos ("que ... envió a costeo el formulario ampliado ...") y el costeo con un
/// conexo son de <see cref="clsPasosAjusteMargen"/> y de <c>clsPasosServiciosConexos</c>: aquí no se repiten.
/// </summary>
[Binding]
public sealed partial class clsPasosMonedaCosteo(clsContextoWeb web, clsPasosCosteo costeo)
{
    private readonly Dictionary<string, string> _duenos = new(StringComparer.OrdinalIgnoreCase);

    [Given("la TRM del día es {int} según la fuente principal")]
    [Given("la TRM del día cambia a {int}")]
    public void DadoLaTrmDelDia(int trm)
    {
        costeo.DolarApi.Valor = trm;
        costeo.DolarApi.FechaActualizacion = clsDolarApiFalso.HoyALasOchoEnBogota();
    }

    [Given("que la fuente principal de la TRM no responde y la de respaldo da {int}")]
    public void DadoQueSoloRespondeElRespaldo(int trm)
    {
        var hoy = DateOnly.FromDateTime(clsDolarApiFalso.HoyALasOchoEnBogota().DateTime);
        costeo.DolarApi.Caida(10);
        costeo.Superfinanciera.Responder(HttpStatusCode.OK, "[" + clsTrmFalso.Fila(trm, hoy, hoy) + "]");
    }

    [Given("que ninguna fuente de la TRM responde")]
    public void DadoQueNingunaFuenteResponde()
    {
        costeo.DolarApi.Caida(10);
        costeo.Superfinanciera.Caida(10);
    }

    [When("{string} envía a costeo en {string} el formulario ampliado del cliente {string} como {string}")]
    public Task CuandoEnviaEnMoneda(string alias, string moneda, string nit, string cotizacion) =>
        EnviarAsync(alias, nit, cotizacion, moneda);

    [Given("que {string} envió a costeo en {string} el formulario ampliado del cliente {string} como {string}")]
    public async Task DadoQueEnvioEnMoneda(string alias, string moneda, string nit, string cotizacion)
    {
        await EnviarAsync(alias, nit, cotizacion, moneda);
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
    }

    [Given("que {string} envió a costeo en {string} el formulario del cliente {string} como {string} con la TRM {int}")]
    public async Task DadoQueEnvioConLaTrm(string alias, string moneda, string nit, string cotizacion, int trm)
    {
        DadoLaTrmDelDia(trm);
        await DadoQueEnvioEnMoneda(alias, moneda, nit, cotizacion);
    }

    /// <summary>
    /// Re-solicitud en USD: la cotización se creó con el formulario en USD y la TRM fijada fue la de un día anterior.
    /// La caché de la TRM fija una sola TRM por día de negocio, así que el "día anterior" se simula cambiando solo la TRM guardada; la moneda
    /// es la que fijó el formulario.
    /// </summary>
    [Given("que la TRM fijada de {string} fue {string} el día que se creó")]
    public async Task DadoQueLaTrmFueOtroDia(string cotizacion, string trm)
    {
        (await costeo.Columna(cotizacion, "moneda")).Should().Be("USD", "la moneda la fija el formulario, no la prueba");
        await clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET trmAplicada = {Decimal(trm).ToString(CultureInfo.InvariantCulture)} "
            + $"WHERE folio = '{costeo.Folio(cotizacion)}'");
    }

    [Then("la cotización {string} está en {string} con la TRM aplicada {int}")]
    public async Task EntoncesLaMonedaYLaTrm(string cotizacion, string moneda, int trm)
    {
        var vista = await CosteoAsync(cotizacion);
        vista.GetProperty("moneda").GetString().Should().Be(moneda);
        vista.GetProperty("trm_aplicada").GetDecimal().Should().Be(trm);
    }

    [Then(@"^el costeo de ""([^""]+)"" tiene el precio final (\d+\.\d+) en ""([^""]+)"" con la TRM aplicada (\d+)$")]
    public async Task EntoncesElPrecioEnMoneda(string cotizacion, string precio, string moneda, string trm)
    {
        var vista = await CosteoAsync(cotizacion);
        vista.GetProperty("precio_final").GetDecimal().Should().Be(Decimal(precio));
        vista.GetProperty("moneda").GetString().Should().Be(moneda);
        vista.GetProperty("trm_aplicada").GetDecimal().Should().Be(Decimal(trm));
    }

    [Then("el costo de {string} guardado es {int} en COP")]
    public async Task EntoncesElCostoEnPesos(string cotizacion, int costo)
    {
        Decimal((await costeo.Columna(cotizacion, "costo"))!).Should().Be(costo, "el costo de Nexo siempre se guarda en pesos");
        (await CosteoAsync(cotizacion)).ToString().Should().NotContain("\"costo\"", "solo el costo bruto de la ruta (costo_final, HU-039.1) llega al dueño");
    }

    [Then(@"^el servicio conexo ""([^""]+)"" de ""([^""]+)"" tiene el precio (\d+\.\d+)$")]
    public async Task EntoncesElConexoEnMoneda(string nombre, string cotizacion, string precio) =>
        (await CosteoAsync(cotizacion)).GetProperty("servicios_conexos").EnumerateArray()
            .Single(s => s.GetProperty("nombre").GetString() == nombre).GetProperty("precio").GetDecimal().Should().Be(Decimal(precio));

    [Then("el PDF de {string} muestra la tarifa {string}")]
    [Then("el PDF de {string} contiene {string}")]
    public async Task EntoncesElPdfContiene(string cotizacion, string texto) =>
        (await TextoPdfAsync(cotizacion)).Should().Contain(texto);

    [Then("el PDF de {string} no contiene {string}")]
    public async Task EntoncesElPdfNoContiene(string cotizacion, string texto) =>
        (await TextoPdfAsync(cotizacion)).Should().NotContain(texto);

    private async Task EnviarAsync(string alias, string nit, string cotizacion, string? moneda)
    {
        await costeo.EnviarACosteoAsync(alias, nit, cotizacion, moneda);
        if (web.UltimoEstado == HttpStatusCode.Created)
        {
            _duenos[cotizacion] = alias;
        }
    }

    /// <summary>GET del costeo con la sesión del dueño (por defecto "ana", la de los antecedentes).</summary>
    private async Task<JsonElement> CosteoAsync(string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo", web.Sesion(Dueno(cotizacion)).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        return web.UltimoJson();
    }

    /// <summary>Texto del PDF vigente de la cotización (descargado por su <c>pdf_url</c>), con los espacios normalizados.</summary>
    private async Task<string> TextoPdfAsync(string cotizacion)
    {
        var url = (await CosteoAsync(cotizacion)).GetProperty("pdf_url").GetString();
        url.Should().NotBeNullOrEmpty("la cotización debe tener el PDF generado");
        await web.EnviarAsync(HttpMethod.Get, new Uri(new Uri("http://localhost"), url).AbsolutePath, web.Sesion(Dueno(cotizacion)).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK);
        return Espacios().Replace(clsLectorPdf.Leer(web.UltimoContenido).Texto, " ");
    }

    private string Dueno(string cotizacion) => _duenos.TryGetValue(cotizacion, out var alias) ? alias : "ana";

    private static decimal Decimal(string valor) => decimal.Parse(valor, NumberStyles.Number, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacios();
}
