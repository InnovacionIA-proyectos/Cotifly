using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Rango válido de un margen según el método de cálculo:
/// <list type="bullet">
/// <item><c>margen</c>: precio = costo / (1 − m), exige 0 ≤ m &lt; 1.</item>
/// <item><c>markup</c> (u otro método): precio = costo × (1 + m), exige m ≥ 0 y admite m ≥ 1.</item>
/// </list>
/// Un método nulo o vacío se trata como <c>margen</c>, el valor por defecto de la columna.
/// </summary>
public static class clsReglaMargen
{
    public const string MetodoMargen = "margen";
    public const string MetodoMarkup = "markup";

    /// <summary>¿El método es <c>margen</c> (o no viene, que equivale al valor por defecto)?</summary>
    public static bool EsMetodoMargen(string? metodoCalculo) =>
        string.IsNullOrWhiteSpace(metodoCalculo)
        || metodoCalculo.Trim().Equals(MetodoMargen, StringComparison.OrdinalIgnoreCase);

    /// <summary>Error si <paramref name="margen"/> está fuera de rango para el método; <c>null</c> si es válido o no viene.</summary>
    public static clsErrorDominio? Validar(string codigoError, string campo, decimal? margen, string? metodoCalculo)
    {
        if (margen is null)
        {
            return null;
        }

        if (margen < 0m)
        {
            return new clsErrorDominio(codigoError, $"El campo '{campo}' no puede ser negativo.");
        }

        if (margen >= 1m && EsMetodoMargen(metodoCalculo))
        {
            return new clsErrorDominio(codigoError, $"El campo '{campo}' debe ser menor que 1 con el método 'margen'.");
        }

        return null;
    }
}
