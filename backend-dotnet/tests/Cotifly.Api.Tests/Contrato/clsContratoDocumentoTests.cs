using System.Globalization;
using System.Reflection;
using Cotifly.Api.Comun;
using Cotifly.Pruebas.Contrato;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// HU-021.11 (#138): el "documento" que la API genera de sí misma (sus endpoints con sus <c>ProducesResponseType</c> y los DTOs de
/// <see cref="clsContratoRutasTests.MapaDtos"/>) contra <c>specs/openapi.yaml</c>:
/// <list type="bullet">
/// <item>cada esquema alcanzable desde una operación de <c>/web/*</c> o <c>/pdfs</c> tiene su DTO (la comparación exacta de propiedades
/// la hace <see cref="clsContratoRutasTests"/>);</item>
/// <item>cada <c>ProducesResponseType</c> de una acción existe en el contrato con el mismo estado y el esquema de su DTO;</item>
/// <item>en los DTOs de respuesta, cada propiedad <c>required</c> y no anulable del esquema tampoco es anulable en C# (si lo fuera, el
/// servidor podría mandar un <c>null</c> que el contrato no permite).</item>
/// </list>
/// </summary>
public sealed class clsContratoDocumentoTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    /// <summary>Esquemas alcanzables desde <c>/web/*</c> que no son un DTO, con el motivo.</summary>
    public static readonly IReadOnlyDictionary<string, string> EsquemasSinDto = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Nulo"] = "Auxiliar de oneOf para un campo en null (OAS 3.0.3 no tiene type: 'null').",
    };

    private static readonly NullabilityInfoContext _nulabilidad = new();

    [Fact]
    public async Task CadaEsquemaAlcanzableDesdeWebTieneSuDto()
    {
        var contrato = await clsValidadorEsquemaOpenApi.ContratoAsync();
        var alcanzables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ruta, item) in contrato.Paths.Where(p => p.Key.StartsWith("/web/", StringComparison.Ordinal) || p.Key.StartsWith("/pdfs/", StringComparison.Ordinal)))
        {
            foreach (var operacion in item.Operations!.Values)
            {
                foreach (var contenido in (operacion.RequestBody?.Content?.Values ?? []).Concat(operacion.Responses!.Values.SelectMany(r => r.Content?.Values ?? [])))
                {
                    Recorrer(contenido.Schema, alcanzables);
                }
            }
        }

        var conDto = clsContratoRutasTests.MapaDtos.Select(m => m.Esquema).ToHashSet(StringComparer.Ordinal);
        alcanzables.Where(e => !conDto.Contains(e) && !EsquemasSinDto.ContainsKey(e)).Should()
            .BeEmpty("cada esquema de /web/* necesita su DTO en clsContratoRutasTests.MapaDtos (o un motivo en EsquemasSinDto)");
        conDto.Concat(EsquemasSinDto.Keys).Should().OnlyContain(e => contrato.Components!.Schemas!.ContainsKey(e), "los esquemas mapeados existen en el contrato");
        EsquemasSinDto.Keys.Should().OnlyContain(e => alcanzables.Contains(e), "una excepción que ya no se usa sobra");
    }

    [Fact]
    public async Task CadaProducesResponseTypeDeUnaAccionEstaEnElContratoConElEsquemaDeSuDto()
    {
        var contrato = await clsValidadorEsquemaOpenApi.ContratoAsync();
        var esquemaDe = clsContratoRutasTests.MapaDtos.GroupBy(m => m.Dto).ToDictionary(g => g.Key, g => g.Select(m => m.Esquema).ToHashSet(StringComparer.Ordinal));
        var errores = new List<string>();
        var revisados = 0;

        foreach (var (metodo, ruta, respuestas) in Acciones())
        {
            if (!contrato.Paths.TryGetValue(ruta, out var item) || item.Operations is null
                || !item.Operations.TryGetValue(new HttpMethod(metodo), out var operacion))
            {
                errores.Add($"{metodo} {ruta}: no está en el contrato");
                continue;
            }

            foreach (var respuesta in respuestas)
            {
                revisados++;
                var estado = respuesta.StatusCode.ToString(CultureInfo.InvariantCulture);
                if (!operacion.Responses!.TryGetValue(estado, out var declarada))
                {
                    errores.Add($"{metodo} {ruta}: el contrato no declara la respuesta {estado}");
                    continue;
                }

                // Un archivo (GET /pdfs/{archivo}) no es JSON: basta con que el estado esté declarado.
                if (respuesta.Type is null || respuesta.Type == typeof(void) || typeof(FileResult).IsAssignableFrom(respuesta.Type))
                {
                    continue;
                }

                var (tipo, esLista) = ElementoDe(respuesta.Type);
                if (!esquemaDe.TryGetValue(tipo, out var esperados))
                {
                    errores.Add($"{metodo} {ruta} {estado}: {tipo.Name} no está en clsContratoRutasTests.MapaDtos");
                    continue;
                }

                var declarados = NombresDe(declarada.Content?.TryGetValue("application/json", out var json) == true ? json.Schema : null, esLista);
                if (!declarados.Overlaps(esperados))
                {
                    errores.Add($"{metodo} {ruta} {estado}: el contrato declara [{string.Join(", ", declarados)}] y la acción produce "
                        + $"{(esLista ? "[]" : string.Empty)}{tipo.Name} ({string.Join(", ", esperados)})");
                }
            }
        }

        revisados.Should().BePositive("las acciones de /web/* declaran sus ProducesResponseType");
        errores.Should().BeEmpty();
    }

    [Fact]
    public async Task LosRequiredNoAnulablesDeLasRespuestasNoSonAnulablesEnLosDtos()
    {
        // Un DTO puede tener dos esquemas (clsEnvioWebRequest: EnvioWeb y BorradorCorreoWeb); aquí solo se miran las respuestas.
        var porTipo = clsContratoRutasTests.MapaDtos.DistinctBy(m => m.Dto).ToDictionary(m => m.Dto, m => m.Esquema);
        var respuestas = new HashSet<Type>();
        foreach (var tipo in Acciones().SelectMany(a => a.Respuestas).Select(r => r.Type).OfType<Type>())
        {
            AgregarConAnidados(ElementoDe(tipo).Tipo, porTipo, respuestas);
        }

        respuestas.Should().Contain(typeof(Cotifly.Aplicacion.Costeo.Services.clsCosteoCotizacion), "GET .../costeo es una respuesta");
        var errores = new List<string>();
        foreach (var dto in respuestas)
        {
            var esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync(porTipo[dto]);
            var propiedades = clsJsonWeb.Opciones.GetTypeInfo(dto).Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var requerido in esquema.Required ?? new HashSet<string>())
            {
                if (!propiedades.TryGetValue(requerido, out var propiedad) || esquema.Properties is null
                    || !esquema.Properties.TryGetValue(requerido, out var deEsquema) || AdmiteNulo(deEsquema))
                {
                    continue;
                }

                if (EsAnulable(propiedad))
                {
                    errores.Add($"{porTipo[dto]}.{requerido} ({dto.Name}): obligatorio y no anulable en el contrato, anulable en C#");
                }
            }
        }

        errores.Should().BeEmpty();
    }

    /// <summary>Acciones de MVC de <c>/web/*</c> y <c>/pdfs</c> con sus <c>ProducesResponseType</c> (ruta como la escribe el contrato).</summary>
    private List<(string Metodo, string Ruta, IReadOnlyList<ProducesResponseTypeAttribute> Respuestas)> Acciones()
    {
        var fuente = factory.Services.GetRequiredService<EndpointDataSource>();
        return [.. fuente.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } r && (r.StartsWith("web/", StringComparison.Ordinal) || r.StartsWith("pdfs/", StringComparison.Ordinal)))
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (
                Metodo: m,
                Ruta: "/" + System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!, ":[^}]+}", "}"),
                Respuestas: (IReadOnlyList<ProducesResponseTypeAttribute>)[.. e.Metadata.OfType<ProducesResponseTypeAttribute>()])))];
    }

    private static (Type Tipo, bool EsLista) ElementoDe(Type tipo)
    {
        if (tipo != typeof(string) && tipo.IsGenericType && tipo.GetGenericArguments() is [var elemento]
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(tipo))
        {
            return (elemento, true);
        }

        return (tipo, false);
    }

    /// <summary>Nombres de los esquemas que admite una respuesta (<c>$ref</c>, <c>oneOf</c> o arreglo de <c>$ref</c>).</summary>
    private static HashSet<string> NombresDe(IOpenApiSchema? esquema, bool esLista)
    {
        var nombres = new HashSet<string>(StringComparer.Ordinal);
        if (esquema is null)
        {
            return nombres;
        }

        if (esLista)
        {
            if (esquema.Items is OpenApiSchemaReference elemento)
            {
                nombres.Add(elemento.Reference.Id!);
            }

            return nombres;
        }

        if (esquema is OpenApiSchemaReference referencia)
        {
            nombres.Add(referencia.Reference.Id!);
        }

        foreach (var alternativa in esquema.OneOf ?? [])
        {
            nombres.UnionWith(NombresDe(alternativa, esLista: false));
        }

        return nombres;
    }

    private static void Recorrer(IOpenApiSchema? esquema, HashSet<string> vistos)
    {
        if (esquema is null)
        {
            return;
        }

        if (esquema is OpenApiSchemaReference referencia && !vistos.Add(referencia.Reference.Id!))
        {
            return;
        }

        foreach (var hijo in (esquema.Properties?.Values ?? []).Concat(esquema.OneOf ?? []).Concat(esquema.AllOf ?? []).Concat(esquema.AnyOf ?? []))
        {
            Recorrer(hijo, vistos);
        }

        Recorrer(esquema.Items, vistos);
    }

    /// <summary>El DTO y los DTOs mapeados de sus propiedades (también los elementos de listas), recursivamente.</summary>
    private static void AgregarConAnidados(Type tipo, IReadOnlyDictionary<Type, string> mapeados, HashSet<Type> respuestas)
    {
        if (!mapeados.ContainsKey(tipo) || !respuestas.Add(tipo))
        {
            return;
        }

        foreach (var propiedad in clsJsonWeb.Opciones.GetTypeInfo(tipo).Properties)
        {
            AgregarConAnidados(ElementoDe(Nullable.GetUnderlyingType(propiedad.PropertyType) ?? propiedad.PropertyType).Tipo, mapeados, respuestas);
        }
    }

    private static bool EsAnulable(System.Text.Json.Serialization.Metadata.JsonPropertyInfo propiedad)
    {
        if (propiedad.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(propiedad.PropertyType) is not null;
        }

        return propiedad.AttributeProvider is PropertyInfo info && _nulabilidad.Create(info).ReadState == NullabilityState.Nullable;
    }

    /// <summary><c>nullable: true</c>, <c>null</c> en el enum o un <c>oneOf</c> con <c>Nulo</c>.</summary>
    private static bool AdmiteNulo(IOpenApiSchema esquema) =>
        (esquema.Type is { } tipo && tipo.HasFlag(JsonSchemaType.Null))
        || (esquema.Enum?.Any(e => e is null || e.GetValueKind() == System.Text.Json.JsonValueKind.Null) ?? false)
        || (esquema.OneOf?.Any(a => a is OpenApiSchemaReference { Reference.Id: "Nulo" } || AdmiteNulo(a)) ?? false);
}
