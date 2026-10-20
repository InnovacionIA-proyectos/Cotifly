using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Aplicacion.Comun;

/// <summary>
/// Huella (SHA-256 en hexadecimal, 64 caracteres) del cuerpo de una petición con <c>Idempotency-Key</c> (#191). La comparten la captura,
/// los conexos, la re-solicitud y la edición: la misma clave con otra huella es <c>IDEMPOTENCY_KEY_REUTILIZADA</c>.
/// </summary>
/// <remarks>
/// Compara POR VALOR los números (deuda del PR #195): <c>1.5</c>, <c>1.50</c> y <c>1.500</c> dan la misma huella, así un reintento que el
/// cliente vuelve a serializar con otra escala de decimales se reconoce como la misma petición y no se ejecuta otra vez. El resto del
/// cuerpo se compara tal cual (JSON con las convenciones web, en el orden de las propiedades del DTO).
/// </remarks>
public static class clsHuellaIdempotencia
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Huella de <paramref name="cuerpo"/> (64 caracteres hexadecimales en mayúscula).</summary>
    public static string Calcular(object? cuerpo)
    {
        var nodo = JsonSerializer.SerializeToNode(cuerpo, _json);
        var normalizado = Normalizar(nodo)?.ToJsonString(_json) ?? "null";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizado)));
    }

    /// <summary>Copia de <paramref name="nodo"/> con cada número escrito sin ceros sobrantes (<c>1.50</c> → <c>1.5</c>, <c>2.0</c> → <c>2</c>).</summary>
    private static JsonNode? Normalizar(JsonNode? nodo) => nodo switch
    {
        JsonObject objeto => new JsonObject(objeto.Select(p => KeyValuePair.Create(p.Key, Normalizar(p.Value)))),
        JsonArray arreglo => new JsonArray([.. arreglo.Select(Normalizar)]),
        JsonValue valor when valor.GetValueKind() == JsonValueKind.Number => JsonValue.Create(Numero(valor)),
        _ => nodo?.DeepClone(),
    };

    /// <summary>
    /// El número como texto canónico: un <c>decimal</c> sin ceros a la derecha (dividir por <c>1.000…</c> quita la escala sobrante);
    /// si no cabe en <c>decimal</c>, el <c>double</c> en formato de ida y vuelta.
    /// </summary>
    private static string Numero(JsonValue valor)
    {
        var texto = valor.ToJsonString();
        if (decimal.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var numero))
        {
            return (numero / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
        }

        return double.Parse(texto, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
    }
}
