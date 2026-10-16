using System.Globalization;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// HU-018.1 (#155), pasos de <c>specs/features/costeo_nexo.feature</c>: "Ver detalle" (<c>GET .../costeo</c>) trae la fecha límite de la
/// solicitud y el <c>scorePrioridad</c> que Nexo devolvió al crear la solicitud (guardado en <c>CotizacionesSolicitud.scorePrioridad</c>).
/// </summary>
[Binding]
public sealed class clsPasosVerDetalle(clsContextoWeb web, clsPasosCosteo costeo)
{
    [Given("que Nexo responde el alta con el score de prioridad {string}")]
    public void DadoElScore(string score) => costeo.Nexo.ScorePrioridadAlta = decimal.Parse(score, CultureInfo.InvariantCulture);

    [Given("que Nexo responde el alta sin score de prioridad")]
    public void DadoSinScore() => costeo.Nexo.ScorePrioridadAlta = null;

    [When("{string} envía a costeo el formulario ampliado del cliente {string} con la fecha límite {string} como {string}")]
    public Task CuandoEnviaConDeadline(string alias, string nit, string deadline, string cotizacion) =>
        costeo.EnviarACosteoAsync(alias, nit, cotizacion, null, deadline);

    [When("{string} abre el costeo de {string}")]
    public Task CuandoAbreElCosteo(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo", web.Sesion(alias).Access);

    [Then("la respuesta trae la fecha límite {string} y el score de prioridad {string}")]
    public void EntoncesTrae(string deadline, string score)
    {
        var json = web.UltimoJson();
        json.GetProperty("deadline").GetString().Should().Be(deadline, "la API responde la fecha límite en UTC, como created_at");
        json.GetProperty("score_prioridad").GetDecimal().Should().Be(decimal.Parse(score, CultureInfo.InvariantCulture));
    }

    [Then("la respuesta trae la fecha límite vacía y el score de prioridad vacío")]
    public void EntoncesVacios()
    {
        var json = web.UltimoJson();
        json.GetProperty("deadline").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("score_prioridad").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Then("la cotización {string} guarda el score de prioridad {string}")]
    public async Task EntoncesGuarda(string cotizacion, string score) =>
        decimal.Parse((await costeo.Columna(cotizacion, "scorePrioridad"))!, CultureInfo.InvariantCulture)
            .Should().Be(decimal.Parse(score, CultureInfo.InvariantCulture));
}
