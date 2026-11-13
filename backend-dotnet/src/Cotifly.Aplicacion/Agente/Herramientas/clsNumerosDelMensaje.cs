using System.Globalization;
using System.Text;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Valores posibles de un número escrito en un texto, según sus separadores. Es la base del filtro de montos del asistente
/// (<c>clsReglasRespuestaAgente.Montos</c>): de él depende que el modelo no escriba un monto que nada respalda (ADR-018 §2b).
/// Lee los formatos habituales en Colombia y los que puede devolver un modelo:
/// <c>2.850.000</c>, <c>2,850,000</c>, <c>2850000</c>, <c>2'850.000</c>, <c>2’850.000</c>, <c>2 850 000</c> (espacio normal, duro o
/// fino), <c>2.850.000,50</c>, <c>2,850,000.50</c> y dígitos de otros sistemas (<c>３.４００</c>), que se pasan a ASCII.
/// </summary>
/// <remarks>
/// Cada ambigüedad de separadores da un candidato (<c>2.850</c> puede ser 2850 o 2,85): el validador acepta el monto si
/// <b>alguno</b> coincide con un respaldo, y lo rechaza si ninguno coincide. Un número que no se puede leer no da candidatos, y el
/// validador lo trata como un monto sin respaldo (nunca lo deja pasar).
/// </remarks>
public static class clsNumerosDelMensaje
{
    /// <summary>Separadores de miles que se ignoran: apóstrofo recto y tipográfico, y los espacios normal, duro y fino.</summary>
    private static readonly char[] _separadoresMiles = ['\'', '’', ' ', ' ', ' ', ' '];

    /// <summary>Valores posibles de un número suelto (sin sufijo de millones ni de miles), sin repetidos y en orden.</summary>
    public static IReadOnlyList<decimal> CandidatosDe(string? numero)
    {
        var candidatos = new List<decimal>();
        if (string.IsNullOrWhiteSpace(numero))
        {
            return candidatos;
        }

        var texto = Normalizar(numero.Trim());
        foreach (var candidato in Candidatos(texto))
        {
            if (!candidatos.Contains(candidato))
            {
                candidatos.Add(candidato);
            }
        }

        return candidatos;
    }

    /// <summary>Quita los separadores de miles que no son punto ni coma y pasa cualquier dígito Unicode a ASCII.</summary>
    private static string Normalizar(string numero)
    {
        var texto = new StringBuilder(numero.Length);
        foreach (var caracter in numero)
        {
            if (Array.IndexOf(_separadoresMiles, caracter) >= 0)
            {
                continue;
            }

            texto.Append(char.IsDigit(caracter) && caracter is < '0' or > '9'
                ? (char)('0' + (int)char.GetNumericValue(caracter))
                : caracter);
        }

        return texto.ToString();
    }

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
}
