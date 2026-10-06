using System.Globalization;
using System.Text.Json;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Lectura del formato OData v2 de <c>RentabClienteSet</c> (<c>{"d":{"results":[{SalesOrgId, TrqType, OrderPartyId, Value}]}}</c>),
/// común al export JSON y a la respuesta en vivo (el Python lo modelaba con <c>_RegistroMargen</c>). Ante cualquier
/// desviación lanza <see cref="FormatException"/>; cada adaptador la traduce a <c>clsFuenteMargenNoDisponibleException</c>.
/// </summary>
internal static class clsFormatoRentabSap
{
    public const string CampoSalesOrg = "SalesOrgId";
    public const string CampoTrqType = "TrqType";
    public const string CampoOrderParty = "OrderPartyId";
    public const string CampoValor = "Value";

    /// <summary>El arreglo <c>d.results</c>.</summary>
    public static JsonElement.ArrayEnumerator Resultados(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object
            || !raiz.TryGetProperty("d", out var d)
            || d.ValueKind != JsonValueKind.Object
            || !d.TryGetProperty("results", out var resultados)
            || resultados.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("no tiene la forma {\"d\":{\"results\":[...]}}");
        }

        return resultados.EnumerateArray();
    }

    /// <summary>Campo de texto obligatorio de una fila (SAP serializa los identificadores como <c>Edm.String</c>).</summary>
    public static string Texto(JsonElement fila, string campo) =>
        fila.ValueKind == JsonValueKind.Object && fila.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()!
            : throw new FormatException($"fila sin '{campo}' de texto");

    /// <summary>
    /// <c>Value</c> de la fila tal como viene de SAP (porcentaje: 13.5 = 13,5 %), sin dividir por 100 (eso lo hace el resolver).
    /// OData v2 serializa <c>Edm.Decimal</c> como texto (<c>"13.5"</c>); también se acepta un número JSON.
    /// </summary>
    public static decimal Valor(JsonElement fila)
    {
        if (fila.ValueKind != JsonValueKind.Object || !fila.TryGetProperty(CampoValor, out var valor))
        {
            throw new FormatException($"fila sin '{CampoValor}'");
        }

        return valor.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(
                valor.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var numero) => numero,
            JsonValueKind.Number when valor.TryGetDecimal(out var numero) => numero,
            _ => throw new FormatException($"'{CampoValor}' no es numérico"),
        };
    }
}
