using System.Globalization;
using System.Text.RegularExpressions;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Números que escribió el comercial en su mensaje (auditoría de #12, riesgo 5): un costo manual solo se acepta si aparece aquí. Lee
/// los formatos habituales en Colombia: <c>2.850.000</c>, <c>2,850,000</c>, <c>2850000</c>, <c>$2.850.000</c>, <c>2'850.000</c>,
/// <c>2.850.000,50</c> y <c>2,85 millones</c> / <c>2.85 M</c>.
/// </summary>
public static partial class clsNumerosDelMensaje
{
    /// <summary>¿El mensaje contiene <paramref name="valor"/> en alguno de los formatos?</summary>
    public static bool Contiene(string? mensaje, decimal valor) => Numeros(mensaje).Contains(valor);

    /// <summary>Valores posibles de cada número del mensaje (cada ambigüedad de separadores da varios candidatos).</summary>
    public static IReadOnlySet<decimal> Numeros(string? mensaje)
    {
        var valores = new HashSet<decimal>();
        if (string.IsNullOrWhiteSpace(mensaje))
        {
            return valores;
        }

        foreach (Match coincidencia in ExpresionNumero().Matches(mensaje))
        {
            var texto = coincidencia.Groups["n"].Value.Replace("'", string.Empty, StringComparison.Ordinal);
            var millones = coincidencia.Groups["m"].Success;
            foreach (var candidato in Candidatos(texto))
            {
                valores.Add(millones ? candidato * 1_000_000m : candidato);
            }
        }

        return valores;
    }

    /// <summary>Valores posibles de un número suelto (sin sufijo de millones); los apóstrofos de miles se ignoran.</summary>
    internal static IEnumerable<decimal> CandidatosDe(string numero) =>
        Candidatos(numero.Replace("'", string.Empty, StringComparison.Ordinal).Replace("’", string.Empty, StringComparison.Ordinal));

    private static IEnumerable<decimal> Candidatos(string texto)
    {
        // Sin separadores de decimales: puntos y comas como miles.
        if (Parsear(texto.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal)) is { } entero)
        {
            yield return entero;
        }

        // Coma decimal (es-CO): 2.850.000,50 o 2,85.
        if (Parsear(texto.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')) is { } conComa)
        {
            yield return conComa;
        }

        // Punto decimal: 2,850,000.50 o 2.85.
        if (Parsear(texto.Replace(",", string.Empty, StringComparison.Ordinal)) is { } conPunto)
        {
            yield return conPunto;
        }
    }

    private static decimal? Parsear(string texto) =>
        decimal.TryParse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var valor) ? valor : null;

    [GeneratedRegex(@"(?<n>\d[\d.,']*\d|\d)\s*(?<m>millones|millón|millon|mill|mm|m)?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionNumero();
}
