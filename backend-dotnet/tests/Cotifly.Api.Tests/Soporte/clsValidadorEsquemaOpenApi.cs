using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cotifly.Pruebas.Contrato;

/// <summary>
/// Lectura de <c>specs/openapi.yaml</c> (copiado a la salida de las pruebas) y validación de un JSON contra un esquema del
/// contrato (SDD, ADR-010). Cubre lo que usa el contrato: tipos, <c>nullable</c>, <c>enum</c>, <c>required</c>, propiedades (sin
/// campos de más cuando el esquema declara propiedades), <c>items</c>, <c>minItems</c>, <c>oneOf</c>/<c>allOf</c>, formatos
/// <c>uuid</c>/<c>date-time</c>/<c>email</c>, <c>pattern</c> y <c>maxLength</c>. Se comparte entre <c>Cotifly.Api.Tests</c> y
/// <c>Cotifly.Aceptacion.Tests</c> (archivo enlazado).
/// </summary>
public static class clsValidadorEsquemaOpenApi
{
    private static readonly SemaphoreSlim _candado = new(1, 1);
    private static OpenApiDocument? _documento;

    public static string RutaContrato { get; } = Path.Combine(AppContext.BaseDirectory, "specs", "openapi.yaml");

    /// <summary>Contrato leído una sola vez por proceso.</summary>
    public static async Task<OpenApiDocument> ContratoAsync()
    {
        if (_documento is not null)
        {
            return _documento;
        }

        await _candado.WaitAsync();
        try
        {
            if (_documento is null)
            {
                var opciones = new OpenApiReaderSettings();
                opciones.AddYamlReader();
                await using var flujo = File.OpenRead(RutaContrato);
                var resultado = await OpenApiDocument.LoadAsync(flujo, "yaml", opciones);
                _documento = resultado.Document ?? throw new InvalidOperationException("No se pudo leer specs/openapi.yaml.");
            }

            return _documento;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>Esquema de <c>components/schemas</c> por nombre.</summary>
    public static async Task<IOpenApiSchema> EsquemaAsync(string nombre) => (await ContratoAsync()).Components!.Schemas![nombre];

    /// <summary>
    /// Esquema de la respuesta <paramref name="estado"/> de la operación <paramref name="metodo"/> <paramref name="ruta"/>
    /// (ruta tal como está en el contrato, ej. <c>/web/cotizaciones/{folio}/enviar</c>), para <c>application/json</c>.
    /// </summary>
    public static async Task<IOpenApiSchema> EsquemaRespuestaAsync(string ruta, HttpMethod metodo, HttpStatusCode estado)
    {
        var operacion = (await ContratoAsync()).Paths[ruta].Operations![metodo];
        var respuesta = operacion.Responses![((int)estado).ToString(CultureInfo.InvariantCulture)];
        return respuesta.Content!["application/json"].Schema!;
    }

    /// <summary>Errores de validación de <paramref name="valor"/> contra <paramref name="esquema"/> (vacío si cumple).</summary>
    public static IReadOnlyList<string> Validar(JsonElement valor, IOpenApiSchema esquema, string ruta = "$")
    {
        ArgumentNullException.ThrowIfNull(esquema);
        var errores = new List<string>();
        ValidarEn(valor, esquema, ruta, errores);
        return errores;
    }

    /// <summary>Valida un cuerpo JSON (texto) contra un esquema.</summary>
    public static IReadOnlyList<string> Validar(string json, IOpenApiSchema esquema)
    {
        using var documento = JsonDocument.Parse(json);
        return Validar(documento.RootElement.Clone(), esquema);
    }

    private static void ValidarEn(JsonElement valor, IOpenApiSchema esquema, string ruta, List<string> errores)
    {
        if (esquema.OneOf is { Count: > 0 } alternativas)
        {
            if (!alternativas.Any(a => Validar(valor, a, ruta).Count == 0))
            {
                errores.Add($"{ruta}: no cumple ninguna alternativa de oneOf");
            }

            return;
        }

        if (esquema.AllOf is { Count: > 0 } todas)
        {
            foreach (var parte in todas)
            {
                if (valor.ValueKind == JsonValueKind.Null && AdmiteNulo(esquema))
                {
                    break;
                }

                ValidarEn(valor, parte, ruta, errores);
            }
        }

        if (valor.ValueKind == JsonValueKind.Null)
        {
            if (!AdmiteNulo(esquema))
            {
                errores.Add($"{ruta}: null no permitido");
            }

            return;
        }

        var tipo = esquema.Type is { } t ? t & ~JsonSchemaType.Null : (JsonSchemaType?)null;
        switch (tipo)
        {
            case JsonSchemaType.Object:
                ValidarObjeto(valor, esquema, ruta, errores);
                break;
            case JsonSchemaType.Array:
                ValidarArreglo(valor, esquema, ruta, errores);
                break;
            case JsonSchemaType.String:
                ValidarTexto(valor, esquema, ruta, errores);
                break;
            case JsonSchemaType.Integer:
                if (valor.ValueKind != JsonValueKind.Number || !valor.TryGetInt64(out _))
                {
                    errores.Add($"{ruta}: se esperaba un entero y llegó {valor.ValueKind}");
                }

                break;
            case JsonSchemaType.Number:
                if (valor.ValueKind != JsonValueKind.Number)
                {
                    errores.Add($"{ruta}: se esperaba un número y llegó {valor.ValueKind}");
                }

                break;
            case JsonSchemaType.Boolean:
                if (valor.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errores.Add($"{ruta}: se esperaba un booleano y llegó {valor.ValueKind}");
                }

                break;
            default:
                if (esquema.Properties is { Count: > 0 })
                {
                    ValidarObjeto(valor, esquema, ruta, errores);
                }

                break;
        }

        ValidarEnum(valor, esquema, ruta, errores);
    }

    private static void ValidarObjeto(JsonElement valor, IOpenApiSchema esquema, string ruta, List<string> errores)
    {
        if (valor.ValueKind != JsonValueKind.Object)
        {
            errores.Add($"{ruta}: se esperaba un objeto y llegó {valor.ValueKind}");
            return;
        }

        var campos = valor.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var requerido in esquema.Required ?? new HashSet<string>())
        {
            if (!campos.Contains(requerido))
            {
                errores.Add($"{ruta}: falta el campo obligatorio '{requerido}'");
            }
        }

        if (esquema.Properties is not { Count: > 0 } propiedades)
        {
            return;
        }

        foreach (var propiedad in valor.EnumerateObject())
        {
            if (propiedades.TryGetValue(propiedad.Name, out var subesquema))
            {
                ValidarEn(propiedad.Value, subesquema, $"{ruta}.{propiedad.Name}", errores);
            }
            else
            {
                errores.Add($"{ruta}: el campo '{propiedad.Name}' no está en el contrato");
            }
        }
    }

    private static void ValidarArreglo(JsonElement valor, IOpenApiSchema esquema, string ruta, List<string> errores)
    {
        if (valor.ValueKind != JsonValueKind.Array)
        {
            errores.Add($"{ruta}: se esperaba un arreglo y llegó {valor.ValueKind}");
            return;
        }

        if (esquema.MinItems is { } minimo && valor.GetArrayLength() < minimo)
        {
            errores.Add($"{ruta}: menos de {minimo} elementos");
        }

        if (esquema.Items is { } items)
        {
            var i = 0;
            foreach (var elemento in valor.EnumerateArray())
            {
                ValidarEn(elemento, items, $"{ruta}[{i++}]", errores);
            }
        }
    }

    private static void ValidarTexto(JsonElement valor, IOpenApiSchema esquema, string ruta, List<string> errores)
    {
        if (valor.ValueKind != JsonValueKind.String)
        {
            errores.Add($"{ruta}: se esperaba un texto y llegó {valor.ValueKind}");
            return;
        }

        var texto = valor.GetString()!;
        if (esquema.MaxLength is { } largo && texto.Length > largo)
        {
            errores.Add($"{ruta}: supera {largo} caracteres");
        }

        if (!string.IsNullOrEmpty(esquema.Pattern) && !Regex.IsMatch(texto, esquema.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            errores.Add($"{ruta}: '{texto}' no cumple el patrón {esquema.Pattern}");
        }

        var valido = esquema.Format switch
        {
            "uuid" => Guid.TryParse(texto, out _),
            "date-time" => DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "email" => texto.Contains('@', StringComparison.Ordinal),
            _ => true,
        };
        if (!valido)
        {
            errores.Add($"{ruta}: '{texto}' no tiene el formato {esquema.Format}");
        }
    }

    private static void ValidarEnum(JsonElement valor, IOpenApiSchema esquema, string ruta, List<string> errores)
    {
        if (esquema.Enum is not { Count: > 0 } valores)
        {
            return;
        }

        var permitido = valores.Any(e => e is not null && JsonNode.DeepEquals(e, JsonNode.Parse(valor.GetRawText())));
        if (!permitido)
        {
            errores.Add($"{ruta}: {valor.GetRawText()} no está en el enum");
        }
    }

    /// <summary><c>nullable: true</c> de OpenAPI 3.0 (el lector lo traduce a <see cref="JsonSchemaType.Null"/>) o <c>null</c> en el enum.</summary>
    private static bool AdmiteNulo(IOpenApiSchema esquema) =>
        (esquema.Type is { } tipo && tipo.HasFlag(JsonSchemaType.Null))
        || (esquema.Enum?.Any(e => e is null || e.GetValueKind() == JsonValueKind.Null) ?? false);
}
