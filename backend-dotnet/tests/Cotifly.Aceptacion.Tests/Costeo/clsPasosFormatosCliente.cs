using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de las plantillas de mapeo y los formatos de lectura de <c>specs/features/licitaciones_excel.feature</c>. Usa el
/// Nexo falso del escenario (<see cref="clsPasosCosteo"/>), con su importación inteligente.
/// </summary>
[Binding]
public sealed class clsPasosFormatosCliente(clsContextoWeb web, clsPasosCosteo costeo)
{
    private const string RutaPlantillas = "/web/licitaciones/plantillas-mapeo";
    private const string RutaRecetas = "/web/licitaciones/recetas-lectura";

    private static string[] Lista(string texto) => [.. texto.Split(',').Select(t => t.Trim())];

    [When("{string} guarda la plantilla de mapeo de la hoja {string} a {string} del cliente {string}")]
    public Task CuandoGuardaLaPlantilla(string alias, string encabezados, string campos, string nit)
    {
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            nombre = "Pliego del cliente",
            encabezados = Lista(encabezados),
            columnas = Lista(campos).Select((c, i) => new { indice = i, campo = c }).ToArray(),
        });
        return web.EnviarAsync(HttpMethod.Put, RutaPlantillas, web.Sesion(alias).Access, cuerpo);
    }

    [When("{string} lista las plantillas de mapeo del cliente {string}")]
    public Task CuandoListaLasPlantillas(string alias, string nit) =>
        web.EnviarAsync(HttpMethod.Get, $"{RutaPlantillas}?nit_cliente={Uri.EscapeDataString(nit)}", web.Sesion(alias).Access);

    [When("{string} lista los formatos de lectura sin el NIT del cliente")]
    public Task CuandoListaLosFormatosSinNit(string alias) => web.EnviarAsync(HttpMethod.Get, RutaRecetas, web.Sesion(alias).Access);

    [Then("la lista trae {int} plantilla(s)")]
    public void EntoncesLaListaTrae(int cantidad) => web.UltimoJson().GetArrayLength().Should().Be(cantidad);

    [Then("Nexo no recibió la consulta de los formatos")]
    public void EntoncesNexoNoRecibioLaConsulta() =>
        costeo.Nexo.Llamadas(HttpMethod.Get, "/api/captura-solicitudes/recetas-lectura").Should().Be(0);

    /// <summary>
    /// La siguiente carga del mismo formato: lo que NEXO responde a <c>sugerir-mapeo</c> con esos encabezados y ese NIT (aquí se le
    /// pregunta directo al Nexo falso, como lo haría el endpoint de Cotifly que lo reenvía).
    /// </summary>
    [Then("la siguiente carga de la hoja {string} del cliente {string} sale de la plantilla sin IA")]
    public async Task EntoncesLaSiguienteCargaLaReconoce(string encabezados, string nit)
    {
        var cuerpo = JsonSerializer.Serialize(new { clienteNit = nit, encabezados = Lista(encabezados), filas = Array.Empty<string[]>() });
        using var cliente = new HttpClient(costeo.Nexo, disposeHandler: false);
        using var peticion = new HttpRequestMessage(HttpMethod.Post, clsNexoFalso.UrlBase + "/api/captura-solicitudes/sugerir-mapeo")
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        };
        peticion.Headers.Add(clsPeticionNexo.EncabezadoIdentidad, "1");
        using var respuesta = await cliente.SendAsync(peticion);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await respuesta.Content.ReadAsStringAsync())!;
        (json["iaUsada"]!.GetValue<bool>(), json["motivoSinIa"]?.GetValue<string>()).Should().Be((false, "PLANTILLA_RECONOCIDA"));
        json["plantillaId"]!.GetValue<Guid>().Should().Be(web.UltimoJson().GetProperty("id").GetGuid());
    }
}
