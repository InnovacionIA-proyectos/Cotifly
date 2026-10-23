using System.Text.Json;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// #191: el <see cref="clsResultado{TValor}"/> que consume una <c>Idempotency-Key</c>, como JSON para la columna <c>resultado</c> de
/// <c>dbo_cotifly.CapturasIdempotentes</c>, y de vuelta. El valor se guarda con <c>System.Text.Json</c> (los resultados de la captura,
/// los conexos, la re-solicitud y la edición son records); el error, con su código, mensaje, detalle y <c>Datos</c>.
/// </summary>
/// <remarks>
/// Al leer, los <c>Datos</c> del error vuelven como tipos simples: texto, <c>bool</c>, <c>long</c> o <c>decimal</c>, diccionarios
/// (<see cref="IReadOnlyDictionary{TKey, TValue}"/>) y listas (de diccionarios si todos sus elementos lo son). La respuesta HTTP que se
/// arma con ellos es la misma de la primera petición.
/// </remarks>
public static class clsResultadoIdempotenteJson
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);

    /// <summary>JSON de <paramref name="resultado"/>.</summary>
    public static string Serializar<T>(clsResultado<T> resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        var guardado = resultado.EsExitoso
            ? new clsGuardado(true, JsonSerializer.SerializeToElement(resultado.Valor, _json), null)
            : new clsGuardado(false, null, new clsErrorGuardado(
                resultado.Error.Codigo, resultado.Error.Mensaje, resultado.Error.Detalle,
                resultado.Error.Datos is null ? null : JsonSerializer.SerializeToElement(resultado.Error.Datos, _json)));
        return JsonSerializer.Serialize(guardado, _json);
    }

    /// <summary>El resultado guardado en <paramref name="json"/> (de <see cref="Serializar{T}"/>).</summary>
    public static clsResultado<T> Deserializar<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var guardado = JsonSerializer.Deserialize<clsGuardado>(json, _json)
            ?? throw new InvalidOperationException("Resultado de idempotencia vacío.");
        if (guardado.Exito)
        {
            var valor = guardado.Valor is { } elemento ? elemento.Deserialize<T>(_json) : default;
            return valor is null
                ? throw new InvalidOperationException($"Resultado de idempotencia sin valor de {typeof(T).Name}.")
                : clsResultado<T>.Exito(valor);
        }

        var error = guardado.Error ?? throw new InvalidOperationException("Resultado de idempotencia fallido sin error.");
        return new clsErrorDominio(error.Codigo, error.Mensaje, error.Detalle)
        {
            Datos = error.Datos is { ValueKind: JsonValueKind.Object } datos ? Diccionario(datos) : null,
        };
    }

    private static IReadOnlyDictionary<string, object?> Diccionario(JsonElement objeto)
    {
        var diccionario = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var propiedad in objeto.EnumerateObject())
        {
            diccionario[propiedad.Name] = Valor(propiedad.Value);
        }

        return diccionario;
    }

    private static object? Valor(JsonElement elemento) => elemento.ValueKind switch
    {
        JsonValueKind.String => elemento.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => elemento.TryGetInt64(out var entero) ? entero
            : elemento.TryGetDecimal(out var numero) ? numero
            : elemento.GetDouble(),
        JsonValueKind.Object => Diccionario(elemento),
        JsonValueKind.Array => Lista(elemento),
        _ => null,
    };

    private static object Lista(JsonElement arreglo)
    {
        if (arreglo.GetArrayLength() > 0 && arreglo.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object))
        {
            return arreglo.EnumerateArray().Select(Diccionario).ToList();
        }

        return arreglo.EnumerateArray().Select(Valor).ToList();
    }

    private sealed record clsGuardado(bool Exito, JsonElement? Valor, clsErrorGuardado? Error);

    private sealed record clsErrorGuardado(string Codigo, string Mensaje, string? Detalle, JsonElement? Datos);
}
