using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Cotifly.Api.Tests.Sistema;

/// <summary><c>GET /health/ready</c> sin base disponible (no requiere Docker).</summary>
public sealed class clsSaludListoEndpointTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    /// <summary>Puerto 1 en loopback: la conexión se rechaza de inmediato.</summary>
    public const string CadenaInalcanzable =
        "Server=tcp:127.0.0.1,1;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=NoImporta.123;Encrypt=False;Connect Timeout=2";

    [Fact]
    public async Task SinCadenaDeConexionResponde503ConLaFormaDelContrato()
    {
        using var cliente = factory.CreateClient();

        var (estado, json) = await ObtenerAsync(cliente);

        estado.Should().Be(HttpStatusCode.ServiceUnavailable);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "detalle");
        json.RootElement.GetProperty("status").GetString().Should().Be("unavailable");
        json.RootElement.GetProperty("detalle").GetString().Should().Contain("ConnectionStrings:Cotifly");
    }

    [Fact]
    public async Task ConUnaBaseInalcanzableResponde503SinExponerLaCadena()
    {
        using var conBaseCaida = ConCadena(factory, CadenaInalcanzable);
        using var cliente = conBaseCaida.CreateClient();

        var (estado, json) = await ObtenerAsync(cliente);
        var texto = json.RootElement.GetRawText();

        estado.Should().Be(HttpStatusCode.ServiceUnavailable);
        json.RootElement.GetProperty("status").GetString().Should().Be("unavailable");
        texto.Should().NotContain("127.0.0.1").And.NotContain("Password").And.NotContain("NoImporta");
    }

    [Fact]
    public async Task GetHealthReadyNoExigeAutenticacion()
    {
        using var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = null;

        var (estado, _) = await ObtenerAsync(cliente);

        estado.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
    }

    internal static WebApplicationFactory<Program> ConCadena(WebApplicationFactory<Program> factory, string cadena) =>
        factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, configuracion) =>
                configuracion.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Cotifly"] = cadena })));

    internal static async Task<(HttpStatusCode Estado, JsonDocument Json)> ObtenerAsync(HttpClient cliente)
    {
        using var respuesta = await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var cuerpo = await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (respuesta.StatusCode, JsonDocument.Parse(cuerpo));
    }
}

/// <summary><c>GET /health/ready</c> con el SQL Server 2019 de Testcontainers.</summary>
[Trait("Categoria", "Integracion")]
public sealed class clsSaludListoIntegracionTests(clsCotiflyApiFactory factory, clsBaseDatosPruebas baseDatos)
    : IClassFixture<clsCotiflyApiFactory>, IClassFixture<clsBaseDatosPruebas>
{
    [Fact]
    public async Task ConLaBaseDisponibleResponde200ConSoloStatusReady()
    {
        using var conBase = clsSaludListoEndpointTests.ConCadena(factory, baseDatos.CadenaCotifly);
        using var cliente = conBase.CreateClient();

        var (estado, json) = await clsSaludListoEndpointTests.ObtenerAsync(cliente);

        estado.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetRawText().Should().Be("""{"status":"ready"}""");
    }

    [Fact]
    public async Task ConUnaBaseSinElScript0002Responde503EsquemaIncompleto()
    {
        using var sin0002 = clsSaludListoEndpointTests.ConCadena(factory, await baseDatos.CadenaSolo0001Async());
        using var cliente = sin0002.CreateClient();

        var (estado, json) = await clsSaludListoEndpointTests.ObtenerAsync(cliente);

        estado.Should().Be(HttpStatusCode.ServiceUnavailable);
        json.RootElement.GetProperty("status").GetString().Should().Be("unavailable");
        json.RootElement.GetProperty("detalle").GetString().Should().Be("esquema incompleto: falta 0002");
    }
}
