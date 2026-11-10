using System.Net;
using System.Text;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// HU-044.2 (#253): pasos de la sugerencia de mapeo de <c>specs/features/licitaciones_excel.feature</c>. Usa el Nexo falso del escenario
/// (<see cref="clsPasosCosteo"/>), con su importación inteligente (HU-044.1, #252).
/// </summary>
[Binding]
public sealed class clsPasosSugerenciaMapeo(clsContextoWeb web, clsPasosCosteo costeo)
{
    private static string[] Lista(string texto) => [.. texto.Split(',').Select(t => t.Trim())];

    [When("{string} pide la sugerencia de mapeo de la hoja {string} del cliente {string}")]
    public Task CuandoPideLaSugerencia(string alias, string encabezados, string nit)
    {
        var columnas = Lista(encabezados);
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            encabezados = columnas,
            filas = new[] { columnas.Select((_, i) => i switch { 0 => "Buenaventura", 1 => "Bogotá", _ => "32" }).ToArray() },
        });
        return web.EnviarAsync(HttpMethod.Post, "/web/licitaciones/sugerir-mapeo", web.Sesion(alias).Access, cuerpo);
    }

    [Given("que el cliente {string} tiene en Nexo la plantilla de mapeo de {string} a {string}")]
    public async Task DadoLaPlantilla(string nit, string encabezados, string campos)
    {
        // En Cotifly, guardar la plantilla llega con #256: aquí se guarda directo en el Nexo falso, como lo haría NEXO.
        var cuerpo = JsonSerializer.Serialize(new
        {
            clienteNit = nit,
            nombre = "Pliego",
            encabezados = Lista(encabezados),
            columnas = Lista(campos).Select((c, i) => new { indice = i, campo = c }).ToArray(),
        });
        using var cliente = new HttpClient(costeo.Nexo, disposeHandler: false);
        using var peticion = new HttpRequestMessage(HttpMethod.Put, clsNexoFalso.UrlBase + "/api/captura-solicitudes/plantillas-mapeo")
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        };
        peticion.Headers.Add(clsPeticionNexo.EncabezadoIdentidad, "1");
        using var respuesta = await cliente.SendAsync(peticion);
        respuesta.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Then("la sugerencia viene de {string} con la IA usada y propone los campos {string}")]
    public void EntoncesConIa(string origen, string campos) => VerificarSugerencia(origen, true, null, campos);

    [Then("la sugerencia viene de {string} sin la IA por {string} y propone los campos {string}")]
    public void EntoncesSinIa(string origen, string motivo, string campos) => VerificarSugerencia(origen, false, motivo, campos);

    private void VerificarSugerencia(string origen, bool iaUsada, string? motivo, string campos)
    {
        var json = web.UltimoJson();
        json.GetProperty("ia_usada").GetBoolean().Should().Be(iaUsada);
        (json.GetProperty("motivo_sin_ia").ValueKind == JsonValueKind.Null ? null : json.GetProperty("motivo_sin_ia").GetString()).Should().Be(motivo);
        var columnas = json.GetProperty("columnas").EnumerateArray().ToList();
        columnas.Select(c => c.GetProperty("campo").GetString()).Should().Equal(Lista(campos));
        columnas.Should().OnlyContain(c => c.GetProperty("origen").GetString() == origen);
    }
}
