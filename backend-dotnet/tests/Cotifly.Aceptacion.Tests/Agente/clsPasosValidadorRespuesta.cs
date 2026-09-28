using System.Globalization;
using Cotifly.Aceptacion.Tests.Web;
using Microsoft.Extensions.AI;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Agente;

/// <summary>
/// Pasos de <c>validador_respuesta_ia.feature</c> (HU-003.10, #16): el guion de la llamada final del modelo (respuestas crudas,
/// válidas o malformadas) y las aserciones sobre el esquema pedido y el mensaje de corrección del reintento.
/// </summary>
[Binding]
public sealed class clsPasosValidadorRespuesta(clsContextoWeb web, clsContextoAgente agente)
{
    [Given("que el modelo cierra el loop sin herramientas")]
    public void DadoCierraElLoop() => agente.Modelo.Responder("fin del loop");

    [Given(@"^que el modelo pide el margen de ""([^""]+)"" y calcula el precio con costo (\d+) y margen ([0-9.]+) sin cerrar el turno$")]
    public void DadoPrecioSinCerrar(string linea, long costo, string margen) =>
        agente.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = linea })
            .Llamar("calcular_precio", new { costo, margen = decimal.Parse(margen, CultureInfo.InvariantCulture), metodo = "markup" })
            .Responder("fin del loop");

    [Given("que la respuesta final del modelo es:")]
    public void DadoRespuestaFinal(string crudo) => agente.Modelo.Responder(crudo);

    [Then("el texto del chat es {string}")]
    public void EntoncesTextoExacto(string texto) => web.UltimoJson().GetProperty("texto").GetString().Should().Be(texto);

    [Then("la llamada final al modelo pidió el esquema JSON {string} sin herramientas")]
    public void EntoncesEsquema(string nombre)
    {
        var final = agente.Modelo.Llamadas[^1];
        final.Herramientas.Should().BeEmpty();
        final.Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>().Which.SchemaName.Should().Be(nombre);
    }

    [Then("el modelo recibió un mensaje de corrección que menciona {string}")]
    public void EntoncesCorreccion(string fragmento) =>
        agente.Modelo.Llamadas
            .Select(l => l.Mensajes[^1])
            .Where(m => m.Role == ChatRole.User && m.Text.StartsWith("Tu respuesta anterior no cumple", StringComparison.Ordinal))
            .Should().ContainSingle("hay un solo reintento")
            .Which.Text.Should().Contain(fragmento);
}
