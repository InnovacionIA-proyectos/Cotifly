using System.Globalization;
using System.Text;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Reglas puras de la resolución de margen (port de <c>mcp_server/margen/base.py</c>): normalización del
/// <c>Value</c> de SAP TM, marca de anomalía y comparación tolerante de nombres de línea de negocio.
/// El rango válido de un margen según el método está en <see cref="clsReglaMargen"/>.
/// </summary>
public static class clsReglasMargen
{
    /// <summary>Límite inferior (inclusivo) del rango normal; por debajo el margen es anómalo.</summary>
    public const decimal UmbralMin = 0m;

    /// <summary>Límite superior (inclusivo) del rango normal (60 %); por encima el margen es anómalo.</summary>
    public const decimal UmbralMax = 0.6m;

    /// <summary>Decimales con que se expresa un margen.</summary>
    public const int Decimales = 6;

    /// <summary>
    /// Convierte el <c>Value</c> de SAP TM (porcentaje) a fracción decimal: 13.5 → 0.135, -19.2 → -0.192
    /// (<c>normalizar_value</c>). Redondea a 6 decimales half-to-even y quita ceros a la derecha.
    /// </summary>
    public static decimal NormalizarValorSap(decimal valorPorcentaje) => Redondear(valorPorcentaje / 100m);

    /// <summary>Margen redondeado a 6 decimales (half-to-even) y sin ceros a la derecha (0.1500 → 0.15).</summary>
    public static decimal Redondear(decimal margen) =>
        Math.Round(margen, Decimales, MidpointRounding.ToEven) / 1.000000000000000000000000000000000m;

    /// <summary>True si el margen cae fuera de [0, 0.6] (negativo o atípico): se marca para validación humana (<c>es_anomalo</c>).</summary>
    public static bool EsAnomalo(decimal margen) => margen < UmbralMin || margen > UmbralMax;

    /// <summary>Texto del margen para los mensajes de <c>detalle</c> (invariante, sin ceros a la derecha).</summary>
    public static string Texto(decimal valor) => (valor / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Clave de comparación de una línea de negocio: sin tildes ni diacríticos, en mayúsculas y sin espacios
    /// (<c>" distribución "</c> → <c>"DISTRIBUCION"</c>, <c>"Almacenamiento 3PL"</c> → <c>"ALMACENAMIENTO3PL"</c>).
    /// La collation <c>CI_AS</c> de la base distingue tildes (deuda de ADR-008), por eso la comparación se hace en memoria.
    /// </summary>
    public static string NormalizarLinea(string? linea)
    {
        if (string.IsNullOrWhiteSpace(linea))
        {
            return string.Empty;
        }

        var descompuesta = linea.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(descompuesta.Length);
        foreach (var c in descompuesta)
        {
            if (char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>¿Dos nombres de línea son la misma línea (sin distinguir tildes, mayúsculas ni espacios)?</summary>
    public static bool MismaLinea(string? a, string? b)
    {
        var clave = NormalizarLinea(a);
        return clave.Length > 0 && string.Equals(clave, NormalizarLinea(b), StringComparison.Ordinal);
    }
}
