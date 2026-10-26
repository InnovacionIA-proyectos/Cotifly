using System.Globalization;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/costo_bruto_ruta.feature</c> (HU-039.1, #218): <c>rutas[].costo_final</c> en el costeo y en el detalle,
/// solo para el dueño, y su ausencia en los demás caminos. Las cotizaciones son las de <see cref="clsPasosCosteo"/> (alias → folio); el
/// envío, el costeo de Nexo, los conexos y el PDF son pasos de <see cref="clsPasosAjusteMargen"/> y <see cref="clsPasosMonedaCosteo"/>.
/// </summary>
[Binding]
public sealed class clsPasosCostoBrutoRuta(clsContextoWeb web, clsPasosCosteo costeo)
{
    [When("{string} consulta el costeo de {string}")]
    public Task CuandoConsultaElCosteo(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo", web.Sesion(alias).Access);

    [When("{string} consulta el detalle de {string}")]
    public Task CuandoConsultaElDetalle(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}", web.Sesion(alias).Access);

    [When("{string} previsualiza el correo del costeo de {string}")]
    public Task CuandoPrevisualiza(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/correo/vista-previa", web.Sesion(alias).Access);

    [Then("la ruta {int} de la respuesta no trae costo final ni precio final")]
    public void EntoncesSinCostoNiPrecio(int orden)
    {
        var ruta = Ruta(web.UltimoJson(), orden);
        ruta.GetProperty("costo_final").ValueKind.Should().Be(JsonValueKind.Null);
        ruta.GetProperty("precio_final_linea").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Then(@"^la ruta (\d+) de la respuesta tiene el costo final (\d+) y el precio final (\d+(?:\.\d+)?)$")]
    public void EntoncesCostoYPrecio(int orden, decimal costo, string precio)
    {
        var ruta = Ruta(web.UltimoJson(), orden);
        ruta.GetProperty("costo_final").GetDecimal().Should().Be(costo, "el costo bruto de la ruta siempre va en COP");
        ruta.GetProperty("precio_final_linea").GetDecimal().Should().Be(decimal.Parse(precio, NumberStyles.Number, CultureInfo.InvariantCulture));
    }

    [Then("la ruta {int} del costeo del detalle tiene el costo final {int}")]
    public void EntoncesCostoEnElDetalle(int orden, int costo)
    {
        var json = web.UltimoJson();
        json.GetProperty("solo_lectura").GetBoolean().Should().BeFalse();
        Ruta(json.GetProperty("costeo"), orden).GetProperty("costo_final").GetDecimal().Should().Be(costo);
    }

    [Then("el detalle es de solo consulta y la ruta {int} de su costeo no trae costo final")]
    public void EntoncesSinCostoParaElGerente(int orden)
    {
        var json = web.UltimoJson();
        json.GetProperty("solo_lectura").GetBoolean().Should().BeTrue();
        Ruta(json.GetProperty("costeo"), orden).GetProperty("costo_final").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// Ni el valor del costo ni ningún campo con el costo (<c>costo*</c>, salvo un <c>costo_final</c> en <c>null</c>) en la última respuesta.
    /// </summary>
    [Then("la respuesta no contiene el costo {int}")]
    public void EntoncesNoContieneElCosto(int costo)
    {
        web.UltimoEstado.Should().Be(System.Net.HttpStatusCode.OK, web.UltimoCuerpo);
        web.UltimoCuerpo.Should().NotContain(costo.ToString(CultureInfo.InvariantCulture), "el costo bruto solo lo ve el dueño en el costeo");
        CamposConCosto(web.UltimoJson(), "$").Should().BeEmpty("ningún otro camino trae costos");
    }

    private static JsonElement Ruta(JsonElement costeoJson, int orden) =>
        costeoJson.GetProperty("rutas").EnumerateArray().Single(r => r.GetProperty("linea_orden").GetInt32() == orden);

    private static IEnumerable<string> CamposConCosto(JsonElement elemento, string ruta) => elemento.ValueKind switch
    {
        JsonValueKind.Object => elemento.EnumerateObject().SelectMany(p =>
            p.Name.StartsWith("costo", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null
                ? [$"{ruta}.{p.Name}"]
                : CamposConCosto(p.Value, $"{ruta}.{p.Name}")),
        JsonValueKind.Array => elemento.EnumerateArray().SelectMany((e, i) => CamposConCosto(e, $"{ruta}[{i}]")),
        _ => [],
    };
}
