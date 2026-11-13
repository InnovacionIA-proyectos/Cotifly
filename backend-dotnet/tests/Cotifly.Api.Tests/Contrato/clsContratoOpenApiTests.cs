using System.Net.Http;
using System.Text.Json;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// Pruebas sobre el contrato congelado <c>specs/openapi.yaml</c> (SDD, ADR-010).
/// El archivo se copia a la salida del proyecto de pruebas desde backend-dotnet/specs.
/// </summary>
public sealed class clsContratoOpenApiTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    private static readonly string _rutaContrato = Path.Combine(AppContext.BaseDirectory, "specs", "openapi.yaml");

    [Fact]
    public async Task ElContratoExisteYSeLeeSinErrores()
    {
        File.Exists(_rutaContrato).Should().BeTrue($"el contrato debe copiarse a {_rutaContrato}");

        var (documento, diagnostico) = await LeerContratoAsync();

        diagnostico.Should().NotBeNull();
        diagnostico!.Errors.Should().BeEmpty();
        documento.Should().NotBeNull();
        documento!.Info.Title.Should().Be("Cotifly API");
    }

    [Fact]
    public async Task ElContratoDeclaraGetHealthSinSeguridad()
    {
        var (documento, _) = await LeerContratoAsync();

        documento!.Paths.Should().ContainKey("/health");
        var operaciones = documento.Paths["/health"].Operations;
        operaciones.Should().NotBeNull().And.ContainKey(HttpMethod.Get);
        operaciones![HttpMethod.Get].Security.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task LaRespuestaRealDeHealthCumpleElEsquemaDelContrato()
    {
        var (documento, _) = await LeerContratoAsync();
        var esquema = documento!.Paths["/health"].Operations![HttpMethod.Get]
            .Responses!["200"].Content!["application/json"].Schema!;

        using var cliente = factory.CreateClient();
        var cuerpo = await cliente.GetStringAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(cuerpo);
        var camposReales = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        camposReales.Should().Contain(esquema.Required!, "la respuesta debe traer todos los campos obligatorios del contrato");
        camposReales.Should().OnlyContain(c => esquema.Properties!.ContainsKey(c), "la respuesta no debe traer campos fuera del contrato");
        var valoresStatus = esquema.Properties!["status"].Enum!.Select(e => e!.GetValue<string>());
        valoresStatus.Should().Contain(json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ElContratoDeclaraGetHealthReadySinSeguridadCon200Y503()
    {
        var (documento, _) = await LeerContratoAsync();

        documento!.Paths.Should().ContainKey("/health/ready");
        var operacion = documento.Paths["/health/ready"].Operations![HttpMethod.Get];
        operacion.Security.Should().NotBeNull().And.BeEmpty();
        operacion.Responses.Should().ContainKeys("200", "503");
    }

    [Fact]
    public async Task LaRespuesta503RealDeHealthReadyCumpleElEsquemaDelContrato()
    {
        var (documento, _) = await LeerContratoAsync();
        var esquema = documento!.Paths["/health/ready"].Operations![HttpMethod.Get]
            .Responses!["503"].Content!["application/json"].Schema!;

        // Sin ConnectionStrings:Cotifly la API responde 503 (ver clsSaludListoEndpointTests).
        using var cliente = factory.CreateClient();
        using var respuesta = await cliente.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        ((int)respuesta.StatusCode).Should().Be(503);
        VerificarContraEsquema(json.RootElement, esquema);
    }

    /// <summary>Campos obligatorios presentes, ningún campo fuera del esquema y <c>status</c> dentro de su enum.</summary>
    internal static void VerificarContraEsquema(JsonElement raiz, IOpenApiSchema esquema)
    {
        var camposReales = raiz.EnumerateObject().Select(p => p.Name).ToList();
        camposReales.Should().Contain(esquema.Required!, "la respuesta debe traer todos los campos obligatorios del contrato");
        camposReales.Should().OnlyContain(c => esquema.Properties!.ContainsKey(c), "la respuesta no debe traer campos fuera del contrato");
        var valoresStatus = esquema.Properties!["status"].Enum!.Select(e => e!.GetValue<string>());
        valoresStatus.Should().Contain(raiz.GetProperty("status").GetString());
    }

    private static async Task<ReadResult> LeerContratoAsync()
    {
        var opciones = new OpenApiReaderSettings();
        opciones.AddYamlReader();
        await using var flujo = File.OpenRead(_rutaContrato);
        return await OpenApiDocument.LoadAsync(flujo, "yaml", opciones, TestContext.Current.CancellationToken);
    }
}
