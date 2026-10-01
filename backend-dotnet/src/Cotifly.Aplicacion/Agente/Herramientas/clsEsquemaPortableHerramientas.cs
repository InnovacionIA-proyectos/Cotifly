using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Declaraciones de las herramientas con un JSON Schema portable (HU-003.6, issue #84; ADR-003 "Esquemas portables"). El esquema que
/// arma Semantic Kernel (<see cref="KernelFunction.JsonSchema"/>, 1.80.1) no lo aceptan todos los proveedores:
/// <list type="bullet">
/// <item>lleva la descripción de la función en una clave no estándar <c>condition</c> en la raíz, y Gemini (también vía OmniRoute)
/// responde <c>400 Unknown name "condition"</c>;</item>
/// <item>declara la nulabilidad como arreglo de tipos (<c>["number","null"]</c>), que la API nativa de Gemini no admite.</item>
/// </list>
/// Aquí se reconstruye el esquema con una lista blanca: <see cref="PalabrasPermitidas"/> y los formatos de
/// <see cref="FormatosPermitidos"/>. Un campo anulable queda con su único tipo no nulo y fuera de <c>required</c>; la descripción va
/// a nivel de función. Los nombres, tipos, descripciones y obligatorios no cambian: los demás proveedores ven lo mismo de antes.
/// </summary>
/// <remarks>
/// El adaptador de OpenAI de Microsoft.Extensions.AI agrega <c>"additionalProperties": false</c> a la raíz de cada declaración al
/// serializar la petición; es parte del contrato de OpenAI y Gemini vía OmniRoute lo acepta (probado el 2026-10-01).
/// </remarks>
public static class clsEsquemaPortableHerramientas
{
    /// <summary>Palabras de JSON Schema que pueden aparecer en una declaración.</summary>
    public static readonly IReadOnlySet<string> PalabrasPermitidas =
        new HashSet<string>(StringComparer.Ordinal) { "type", "properties", "required", "description", "enum", "items", "format" };

    /// <summary>Valores de <c>format</c> que entienden OpenAI y Gemini; cualquier otro se quita.</summary>
    public static readonly IReadOnlySet<string> FormatosPermitidos =
        new HashSet<string>(StringComparer.Ordinal) { "date-time", "int32", "int64", "float", "double" };

    private const string Nulo = "null";

    /// <summary>
    /// Declaraciones portables (solo nombre, descripción y esquema; el modelo nunca invoca la función directamente).
    /// <paramref name="tipados"/> reemplaza el esquema de parámetros que Semantic Kernel declara sin tipo (herramienta → parámetro →
    /// esquema), como <see cref="clsEsquemaPayloadPdf.PorHerramienta"/>; la descripción del parámetro se conserva.
    /// </summary>
    public static IList<AITool> Declarar(
        IEnumerable<KernelFunction> funciones, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? tipados = null)
    {
        ArgumentNullException.ThrowIfNull(funciones);
        return funciones.Select(f => (AITool)Declarar(f, tipados is not null && tipados.TryGetValue(f.Name, out var propios) ? propios : null)).ToList();
    }

    /// <summary>Declaración portable de una función: la descripción a nivel de función y el esquema de <see cref="Portable"/>.</summary>
    public static AIFunctionDeclaration Declarar(KernelFunction funcion, IReadOnlyDictionary<string, JsonElement>? tipados = null)
    {
        ArgumentNullException.ThrowIfNull(funcion);
        var descripcion = string.IsNullOrWhiteSpace(funcion.Description) ? DescripcionDelEsquema(funcion.JsonSchema) : funcion.Description;
        var esquema = tipados is { Count: > 0 } ? ConTipados(funcion.JsonSchema, tipados) : funcion.JsonSchema;
        return AIFunctionFactory.CreateDeclaration(funcion.Name, descripcion, Portable(esquema));
    }

    /// <summary>Reemplaza el esquema de cada parámetro de <paramref name="tipados"/> que exista, conservando su descripción.</summary>
    private static JsonElement ConTipados(JsonElement esquema, IReadOnlyDictionary<string, JsonElement> tipados)
    {
        if (esquema.ValueKind != JsonValueKind.Object || JsonNode.Parse(esquema.GetRawText()) is not JsonObject raiz
            || raiz["properties"] is not JsonObject propiedades)
        {
            return esquema;
        }

        foreach (var (nombre, tipado) in tipados)
        {
            if (propiedades[nombre] is not JsonObject original || JsonNode.Parse(tipado.GetRawText()) is not JsonObject nuevo)
            {
                continue;
            }

            if (original["description"] is { } descripcion)
            {
                nuevo["description"] = descripcion.DeepClone();
            }

            propiedades[nombre] = nuevo;
        }

        return JsonSerializer.SerializeToElement(raiz);
    }

    /// <summary>
    /// Esquema de parámetros portable: raíz <c>{"type":"object","properties":{...},"required":[...]}</c> y, en cada propiedad, solo
    /// las palabras permitidas. La nulabilidad (<c>["T","null"]</c>, <c>nullable</c> o <c>anyOf</c> con <c>null</c>) se expresa como
    /// el tipo <c>T</c> sin el campo en <c>required</c>.
    /// </summary>
    public static JsonElement Portable(JsonElement esquema)
    {
        var raiz = esquema.ValueKind == JsonValueKind.Object ? JsonNode.Parse(esquema.GetRawText())!.AsObject() : [];
        var portable = Nodo(raiz, out _);
        portable.Remove("description");
        portable["type"] = "object";
        portable["properties"] ??= new JsonObject();
        return JsonSerializer.SerializeToElement(portable);
    }

    private static string? DescripcionDelEsquema(JsonElement esquema)
    {
        if (esquema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var clave in (string[])["description", "condition"])
        {
            if (esquema.TryGetProperty(clave, out var valor) && valor.ValueKind == JsonValueKind.String)
            {
                return valor.GetString();
            }
        }

        return null;
    }

    private static JsonObject Nodo(JsonObject origen, out bool anulable)
    {
        anulable = origen["nullable"] is JsonValue marca && marca.TryGetValue<bool>(out var esNulable) && esNulable;
        var destino = new JsonObject();

        // anyOf/oneOf con una sola alternativa no nula (patrón de nulabilidad de algunos generadores): se aplana a esa alternativa.
        foreach (var compuesta in (string[])["anyOf", "oneOf"])
        {
            if (origen[compuesta] is JsonArray alternativas)
            {
                var noNulas = alternativas.OfType<JsonObject>().Where(a => !EsSoloNulo(a)).ToList();
                anulable |= noNulas.Count < alternativas.Count;
                if (noNulas.Count == 1)
                {
                    var alternativa = Nodo(noNulas[0], out var internoAnulable);
                    foreach (var (clave, valor) in alternativa)
                    {
                        destino[clave] = valor?.DeepClone();
                    }

                    anulable |= internoAnulable;
                }
            }
        }

        if (origen["type"] is { } tipo && Tipo(tipo, ref anulable) is { } unico)
        {
            destino["type"] = unico;
        }

        if (origen["description"] is JsonValue descripcion && descripcion.TryGetValue<string>(out var textoDescripcion))
        {
            destino["description"] = textoDescripcion;
        }

        if (origen["format"] is JsonValue formato && formato.TryGetValue<string>(out var textoFormato) && FormatosPermitidos.Contains(textoFormato))
        {
            destino["format"] = textoFormato;
        }

        if (origen["enum"] is JsonArray valores)
        {
            var sinNulos = valores.Where(v => v is not null).Select(v => v!.DeepClone()).ToArray();
            anulable |= sinNulos.Length < valores.Count;
            if (sinNulos.Length > 0)
            {
                destino["enum"] = new JsonArray(sinNulos);
            }
        }

        if (origen["items"] is JsonObject elementos)
        {
            destino["items"] = Nodo(elementos, out _);
        }

        if (origen["properties"] is JsonObject propiedades)
        {
            var portables = new JsonObject();
            var anulables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (nombre, valor) in propiedades)
            {
                var propiedadAnulable = false;
                var propiedad = valor is JsonObject objeto ? Nodo(objeto, out propiedadAnulable) : [];
                if (propiedadAnulable)
                {
                    anulables.Add(nombre);
                }

                portables[nombre] = propiedad;
            }

            destino["properties"] = portables;
            if (origen["required"] is JsonArray requeridos)
            {
                var obligatorios = requeridos
                    .Select(r => r is JsonValue v && v.TryGetValue<string>(out var n) ? n : null)
                    .Where(n => n is not null && portables.ContainsKey(n) && !anulables.Contains(n))
                    .Distinct(StringComparer.Ordinal)
                    .Select(n => (JsonNode)n!)
                    .ToArray();
                if (obligatorios.Length > 0)
                {
                    destino["required"] = new JsonArray(obligatorios);
                }
            }
        }

        return destino;
    }

    /// <summary>Un solo tipo no nulo: <c>"T"</c> o <c>["T","null"]</c>. Con varios tipos no nulos no se declara tipo (cualquiera).</summary>
    private static string? Tipo(JsonNode tipo, ref bool anulable)
    {
        if (tipo is JsonValue valor && valor.TryGetValue<string>(out var texto))
        {
            if (texto == Nulo)
            {
                anulable = true;
                return null;
            }

            return texto;
        }

        if (tipo is not JsonArray tipos)
        {
            return null;
        }

        var nombres = tipos.Select(t => t is JsonValue v && v.TryGetValue<string>(out var n) ? n : null).Where(n => n is not null).ToList();
        anulable |= nombres.Contains(Nulo);
        var noNulos = nombres.Where(n => n != Nulo).Distinct(StringComparer.Ordinal).ToList();
        return noNulos.Count == 1 ? noNulos[0] : null;
    }

    private static bool EsSoloNulo(JsonObject alternativa) =>
        alternativa["type"] is JsonValue tipo && tipo.TryGetValue<string>(out var texto) && texto == Nulo;
}
