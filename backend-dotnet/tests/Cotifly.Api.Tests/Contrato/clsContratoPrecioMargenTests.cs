using System.Reflection;
using System.Text.Json;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// Los DTOs de Precio y Margen (#10) y los códigos de error cumplen <c>specs/openapi.yaml</c> (SDD, ADR-010).
/// Se serializan en snake_case, la convención de <c>/web/*</c> (ADR-002; en la Api la aplica <c>clsJsonWeb</c>, #12).
/// </summary>
public sealed class clsContratoPrecioMargenTests
{
    private static readonly string _rutaContrato = Path.Combine(AppContext.BaseDirectory, "specs", "openapi.yaml");
    private static readonly JsonSerializerOptions _snakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task LosCodigosDeErrorDelDominioExistenEnErrorNegocio()
    {
        var esquemas = await EsquemasAsync();
        var codigosContrato = esquemas["ErrorNegocio"].Properties!["error"].Properties!["codigo"].Enum!
            .Select(e => e!.GetValue<string>()).ToList();

        var codigos = typeof(clsCodigosError).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        codigos.Should().NotBeEmpty().And.OnlyContain(c => codigosContrato.Contains(c));
        codigosContrato.Should().Contain(clsCodigosError.AjusteMargenInvalido, "código nuevo de #10 agregado al contrato");
    }

    [Fact]
    public async Task SalidaPrecioCumpleElEsquema()
    {
        var esquemas = await EsquemasAsync();

        Verificar(new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "markup", 444_798m), esquemas["SalidaPrecio"], "metodo");
    }

    // HU-012.4 (auditoría): los errores de las herramientas van en RespuestaChat.errores con el esquema ErrorHerramienta.
    [Fact]
    public async Task LosErroresDeHerramientaDeRespuestaChatCumplenElEsquema()
    {
        var esquemas = await EsquemasAsync();
        var respuesta = esquemas["RespuestaChat"];
        respuesta.Required.Should().NotContain("errores", "el campo es aditivo y opcional");
        var error = esquemas["ErrorHerramienta"];

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            new Cotifly.Aplicacion.Agente.DTOs.clsErrorHerramienta("calcular_precio", clsCodigosError.TrmNoDisponible), _snakeCase));
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(error.Properties!.Keys).And.Contain(error.Required!);
        respuesta.Properties!.Should().ContainKey("errores");
    }

    // HU-012.2: el precio en dólares trae moneda, TRM y precio en pesos, todos declarados en el contrato.
    [Fact]
    public async Task SalidaPrecioEnDolaresCumpleElEsquema()
    {
        var esquemas = await EsquemasAsync();
        var enDolares = new clsSalidaPrecio(
            996.35m, 2_850_000m, 0.135m, "margen", 444_798m, "USD", 3306.86m, 3_294_798m, new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 28));

        Verificar(enDolares, esquemas["SalidaPrecio"], "metodo");
        Verificar(enDolares, esquemas["SalidaPrecio"], "moneda");
    }

    [Theory]
    [InlineData(OrigenMargen.SapTm, "OPTN")]
    [InlineData(OrigenMargen.PoliticaLinea, null)]
    [InlineData(OrigenMargen.Negociado, "OPTN")]
    public async Task SalidaMargenCumpleElEsquema(OrigenMargen origen, string? trqType)
    {
        var esquemas = await EsquemasAsync();

        Verificar(new clsSalidaMargen(0.135m, clsCodigoEnum.ACodigo(origen), trqType, false, "detalle"), esquemas["SalidaMargen"], "origen");
    }

    private static void Verificar(object dto, IOpenApiSchema esquema, string campoEnum)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, dto.GetType(), _snakeCase));
        var campos = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        campos.Should().Contain(esquema.Required!, "el DTO debe traer todos los campos obligatorios del contrato");
        campos.Should().OnlyContain(c => esquema.Properties!.ContainsKey(c), "el DTO no debe traer campos fuera del contrato");
        esquema.Properties![campoEnum].Enum!.Select(e => e!.GetValue<string>())
            .Should().Contain(json.RootElement.GetProperty(campoEnum).GetString());
    }

    private static async Task<IDictionary<string, IOpenApiSchema>> EsquemasAsync()
    {
        var opciones = new OpenApiReaderSettings();
        opciones.AddYamlReader();
        await using var flujo = File.OpenRead(_rutaContrato);
        var resultado = await OpenApiDocument.LoadAsync(flujo, "yaml", opciones, TestContext.Current.CancellationToken);
        return resultado.Document!.Components!.Schemas!;
    }
}
