using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// #181 (Nexo #309), pasos de <c>specs/features/costeo_nexo.feature</c>: el estado de la línea de Nexo y el motivo por el que sigue
/// pendiente se leen con el polling y se muestran por camión en <c>GET .../costeo</c>, sin guardarse en la base. Con un Nexo anterior a
/// #309 (sin esos campos) salen en <c>null</c>.
/// </summary>
[Binding]
public sealed class clsPasosMotivoPendiente(clsContextoWeb web, clsPasosCosteo costeo)
{
    [Given("que Nexo expone el estado y el motivo pendiente de cada línea")]
    public void DadoQueNexoExpone() => costeo.Nexo.ExponerSituacionLinea = true;

    [When("Nexo deja pendiente la línea de {string} con el motivo {string}")]
    public async Task CuandoNexoDejaPendiente(string cotizacion, string motivo) =>
        costeo.Nexo.MarcarMotivoPendiente((await costeo.Columna(cotizacion, "costeoId"))!, 0, motivo);

    [Then("el camión de {string} está {string} en Nexo con el motivo {string}")]
    public void EntoncesConMotivo(string cotizacion, string estado, string motivo)
    {
        var camion = Camion(cotizacion);
        camion.GetProperty("estado_linea_nexo").GetString().Should().Be(estado);
        camion.GetProperty("motivo_pendiente").GetString().Should().Be(motivo);
    }

    [Then("el camión de {string} está {string} en Nexo sin motivo pendiente")]
    public void EntoncesSinMotivo(string cotizacion, string estado)
    {
        var camion = Camion(cotizacion);
        camion.GetProperty("estado_linea_nexo").GetString().Should().Be(estado);
        camion.GetProperty("motivo_pendiente").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Then("el camión de {string} sale sin estado de línea de Nexo ni motivo pendiente")]
    public void EntoncesSinNada(string cotizacion)
    {
        var camion = Camion(cotizacion);
        camion.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        camion.GetProperty("estado_linea_nexo").ValueKind.Should().Be(JsonValueKind.Null);
        camion.GetProperty("motivo_pendiente").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>El único camión de la única ruta en la última respuesta de <c>GET .../costeo</c>.</summary>
    private JsonElement Camion(string cotizacion)
    {
        var json = web.UltimoJson();
        json.GetProperty("folio").GetString().Should().Be(costeo.Folio(cotizacion));
        return json.GetProperty("rutas").EnumerateArray().Should().ContainSingle().Subject
            .GetProperty("camiones").EnumerateArray().Should().ContainSingle().Subject;
    }
}
