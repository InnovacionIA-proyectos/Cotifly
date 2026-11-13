using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Declaraciones de las herramientas con un JSON Schema portable (ADR-003 "Esquemas portables"). El esquema que
/// arma Semantic Kernel (<see cref="KernelFunction.JsonSchema"/>, 1.80.1) no lo aceptan todos los proveedores:
/// <list type="bullet">
/// <item>lleva la descripción de la función en una clave no estándar <c>condition</c> en la raíz, y Gemini (también vía OmniRoute)
/// responde <c>400 Unknown name "condition"</c>;</item>
/// <item>declara la nulabilidad como arreglo de tipos (<c>["number","null"]</c>), que la API nativa de Gemini no admite.</item>
/// </list>
/// Aquí se reconstruye el esquema con una lista blanca: <see cref="PalabrasPermitidas"/> y los formatos de
/// <see cref="FormatosPermitidos"/>. Un campo anulable queda con su único tipo no nulo y fuera de <c>required</c>; la descripción va
/// a nivel de función. Los nombres, tipos, descripciones y obligatorios no cambian: los demás proveedores ven el mismo contrato.
/// Lo que no tiene forma portable (un campo sin tipo, un objeto sin propiedades, un arreglo sin <c>items</c>, uniones, tuplas,
/// diccionarios o <c>$ref</c>) no se degrada en silencio: <see cref="Declarar(KernelFunction, IReadOnlyDictionary{string, JsonElement}?)"/>
/// lanza <see cref="InvalidOperationException"/> con la ruta del campo, y la prueba de contrato lo detecta antes de desplegar.
/// </summary>
/// <remarks>
/// El adaptador de OpenAI de Microsoft.Extensions.AI agrega <c>"additionalProperties": false</c> a la raíz de cada declaración al
/// serializar la petición; es parte del contrato de OpenAI y Gemini vía OmniRoute lo acepta.
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
    /// esquema); la descripción del parámetro se conserva.
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
        var portable = Nodo(raiz, "parameters", esRaiz: true, out _);
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

    /// <summary>
    /// Convierte un nodo. Lanza <see cref="InvalidOperationException"/> con la ruta del campo ante lo que no tiene forma portable
    /// (falla al declarar, no en el proveedor): <c>$ref</c>, <c>anyOf</c>/<c>oneOf</c> con más de una alternativa no nula o los dos
    /// juntos, varios tipos no nulos, tuplas (<c>items</c> en arreglo), diccionarios (<c>additionalProperties</c> con esquema), un
    /// campo sin tipo, un objeto sin propiedades o un arreglo sin <c>items</c>. La raíz puede no tener propiedades.
    /// </summary>
    private static JsonObject Nodo(JsonObject origen, string ruta, bool esRaiz, out bool anulable)
    {
        anulable = origen["nullable"] is JsonValue marca && marca.TryGetValue<bool>(out var esNulable) && esNulable;
        var destino = new JsonObject();
        if (origen.ContainsKey("$ref"))
        {
            throw NoPortable(ruta, "usa $ref; declara el tipo en línea");
        }

        if (origen.ContainsKey("anyOf") && origen.ContainsKey("oneOf"))
        {
            throw NoPortable(ruta, "combina anyOf y oneOf");
        }

        // anyOf/oneOf con una sola alternativa no nula (patrón de nulabilidad de algunos generadores): se aplana a esa alternativa.
        foreach (var compuesta in (string[])["anyOf", "oneOf"])
        {
            if (origen[compuesta] is JsonArray alternativas)
            {
                var noNulas = alternativas.OfType<JsonObject>().Where(a => !EsSoloNulo(a)).ToList();
                if (noNulas.Count != 1)
                {
                    throw NoPortable(ruta, $"{compuesta} con {noNulas.Count} alternativas no nulas (solo se admite T o T|null)");
                }

                anulable |= noNulas.Count < alternativas.Count;
                var alternativa = Nodo(noNulas[0], ruta, esRaiz: false, out var internoAnulable);
                foreach (var (clave, valor) in alternativa)
                {
                    destino[clave] = valor?.DeepClone();
                }

                anulable |= internoAnulable;
            }
        }

        if (origen["items"] is JsonArray)
        {
            throw NoPortable(ruta, "es una tupla (items en arreglo)");
        }

        if (origen["additionalProperties"] is JsonObject)
        {
            throw NoPortable(ruta, "es un diccionario (additionalProperties con esquema)");
        }

        if (origen["type"] is { } tipo && Tipo(tipo, ruta, ref anulable) is { } unico)
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
            destino["items"] = Nodo(elementos, ruta + "[]", esRaiz: false, out _);
        }

        if (origen["properties"] is JsonObject propiedades)
        {
            var portables = new JsonObject();
            var anulables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (nombre, valor) in propiedades)
            {
                var propiedadAnulable = false;
                var propiedad = valor is JsonObject objeto
                    ? Nodo(objeto, $"{ruta}.{nombre}", esRaiz: false, out propiedadAnulable)
                    : throw NoPortable($"{ruta}.{nombre}", "no es un esquema (objeto JSON)");
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

        if (!esRaiz)
        {
            var tipoFinal = destino["type"]?.GetValue<string>();
            if (tipoFinal is null)
            {
                throw NoPortable(ruta, "no declara type (Gemini lo exige en cada campo)");
            }

            if (tipoFinal == "object" && destino["properties"] is not JsonObject { Count: > 0 })
            {
                throw NoPortable(ruta, "es un objeto sin properties (Gemini rechaza OBJECT vacío)");
            }

            if (tipoFinal == "array" && destino["items"] is null)
            {
                throw NoPortable(ruta, "es un arreglo sin items");
            }
        }

        return destino;
    }

    private static InvalidOperationException NoPortable(string ruta, string motivo) =>
        new($"El esquema de la herramienta no es portable: '{ruta}' {motivo}.");

    /// <summary>Un solo tipo no nulo: <c>"T"</c> o <c>["T","null"]</c>. Varios tipos no nulos no son portables.</summary>
    private static string? Tipo(JsonNode tipo, string ruta, ref bool anulable)
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
        return noNulos.Count switch
        {
            1 => noNulos[0],
            0 => null,
            _ => throw NoPortable(ruta, $"admite varios tipos ({string.Join(", ", noNulos)})"),
        };
    }

    private static bool EsSoloNulo(JsonObject alternativa) =>
        alternativa["type"] is JsonValue tipo && tipo.TryGetValue<string>(out var texto) && texto == Nulo;
}
