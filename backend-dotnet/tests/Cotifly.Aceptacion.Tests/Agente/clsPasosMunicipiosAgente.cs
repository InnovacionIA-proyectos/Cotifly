using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Pruebas.Comun;
using Reqnroll;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Cotifly.Aceptacion.Tests.Agente;

/// <summary>
/// Pasos de <c>chat_municipios.feature</c> (HU-005.13): el catálogo de municipios de Nexo falso, el modelo guionizado que llama a
/// <c>resolver_municipio</c> y <c>buscar_tarifa</c>, y aserciones contra lo que recibió el modelo, la fuente de tarifas falsa y el
/// estado guardado en <c>Sesiones.estadoAgente</c>.
/// </summary>
[Binding]
public sealed class clsPasosMunicipiosAgente(clsContextoWeb web, clsContextoAgente agente)
{
    [Given("el catálogo de municipios de Nexo tiene:")]
    public void DadoElCatalogo(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        foreach (var fila in tabla.Rows)
        {
            agente.Municipios.Filas.Add(new clsMunicipio(agente.Municipios.Filas.Count + 1, null, fila["municipio"], fila["departamento"], "Colombia"));
        }
    }

    [Given("que el catálogo de municipios no está disponible")]
    public void DadoSinCatalogo() => agente.Municipios.Disponible = false;

    [Given(@"^que en el turno (\d+) el modelo resuelve el origen ""([^""]+)"" y busca la tarifa ""([^""]+)"" de ""([^""]+)"" a ""([^""]+)""$")]
    public void DadoTurnoQueResuelveYBusca(int turno, string texto, string linea, string origen, string destino) =>
        agente.Modelo
            .Llamar("resolver_municipio", new { texto, campo = "origen" })
            .Llamar("buscar_tarifa", new { lineaNegocio = linea, origen, destino })
            .Cerrar($"Turno {turno}: revisé el origen y la tarifa.");

    [Given(@"^que en el turno (\d+) el modelo busca la tarifa ""([^""]+)"" de ""([^""]+)"" a ""([^""]+)""$")]
    public void DadoTurnoQueBusca(int turno, string linea, string origen, string destino) =>
        agente.Modelo
            .Llamar("buscar_tarifa", new { lineaNegocio = linea, origen, destino })
            .Cerrar($"Turno {turno}: busqué la tarifa.");

    [Then("la herramienta {string} respondió al modelo el municipio {string} de {string}")]
    public void EntoncesMunicipio(string herramienta, string municipio, string departamento)
    {
        var salida = UltimoResultado(herramienta);
        salida.GetProperty("coincidencia").GetString().Should().Be("unica");
        salida.GetProperty("municipio").GetProperty("municipio").GetString().Should().Be(municipio);
        salida.GetProperty("municipio").GetProperty("departamento").GetString().Should().Be(departamento);
    }

    [Then("la herramienta {string} respondió al modelo los candidatos de {string}")]
    public void EntoncesCandidatos(string herramienta, string departamentos)
    {
        var salida = UltimoResultado(herramienta);
        salida.GetProperty("coincidencia").GetString().Should().Be("varias");
        salida.GetProperty("candidatos").EnumerateArray().Select(c => c.GetProperty("departamento").GetString())
            .Should().Equal(departamentos.Split(", "));
    }

    [Then("no se buscó ninguna tarifa")]
    public void EntoncesSinTarifa() => agente.Tarifas.Entradas.Should().BeEmpty("con un municipio ambiguo no se cotiza");

    [Then("la tarifa se buscó con el origen {string}")]
    public void EntoncesTarifaConOrigen(string origen) =>
        agente.Tarifas.Entradas.Should().ContainSingle().Which.Origen.Should().Be(origen);

    [Then("la sesión de {string} guardó el origen {string} verificado en el catálogo")]
    public async Task EntoncesOrigenVerificado(string alias, string origen)
    {
        var fila = await EstadoAsync(alias, "CONCAT(JSON_VALUE(estadoAgente, '$.origen'), '|', JSON_VALUE(estadoAgente, '$.origenMunicipio.municipio'), ', ', JSON_VALUE(estadoAgente, '$.origenMunicipio.departamento'))");
        fila.Should().Be($"{origen}|{origen}");
    }

    [Then("la sesión de {string} tiene {int} municipios candidatos para el origen")]
    public async Task EntoncesCandidatosGuardados(string alias, int cantidad) =>
        int.Parse(await EstadoAsync(alias, "CAST((SELECT COUNT(*) FROM OPENJSON(estadoAgente, '$.candidatosOrigen')) AS NVARCHAR(10))"), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(cantidad);

    private JsonElement UltimoResultado(string herramienta)
    {
        var mensajes = agente.Modelo.Llamadas[^1].Mensajes;
        var llamadas = mensajes.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Where(l => l.Name == herramienta).Select(l => l.CallId).ToHashSet();
        var resultado = mensajes.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault(r => llamadas.Contains(r.CallId));
        resultado.Should().NotBeNull($"el modelo pidió {herramienta}");
        return JsonDocument.Parse(resultado!.Result!.ToString()!).RootElement.Clone();
    }

    /// <summary>Una expresión sobre <c>estadoAgente</c> de la sesión web del comercial (<c>spaceId = threadId = web/{sesion_id}</c>).</summary>
    private async Task<string> EstadoAsync(string alias, string expresion)
    {
        var hilo = $"web/{agente.SesionWeb($"sesion-{alias}")}";
        var valor = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT {expresion} FROM dbo_cotifly.Sesiones WHERE spaceId = '{hilo}' AND threadId = '{hilo}'");
        valor.Should().NotBeNull($"la sesión {hilo} existe");
        web.Sesion(alias).Should().NotBeNull();
        return valor!;
    }
}
