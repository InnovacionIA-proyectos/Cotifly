using System.Net.Http;
using System.Text.Json;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// Pruebas sobre el contrato congelado <c>specs/openapi.yaml</c> (#17, SDD, ADR-010).
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

    private static async Task<ReadResult> LeerContratoAsync()
    {
        var opciones = new OpenApiReaderSettings();
        opciones.AddYamlReader();
        await using var flujo = File.OpenRead(_rutaContrato);
        return await OpenApiDocument.LoadAsync(flujo, "yaml", opciones, TestContext.Current.CancellationToken);
    }
}
