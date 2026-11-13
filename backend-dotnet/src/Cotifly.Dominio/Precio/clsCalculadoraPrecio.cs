using System.Globalization;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Precio;

/// <summary>
/// Cálculo determinista del precio. El LLM nunca hace la aritmética.
/// <list type="bullet">
/// <item><c>margen</c>: precio = costo / (1 − m) (margen sobre el precio de venta), exige 0 ≤ m &lt; 1.</item>
/// <item><c>markup</c>: precio = costo × (1 + m) (recargo sobre el costo), exige m ≥ 0 y admite m ≥ 1.</item>
/// </list>
/// El precio se redondea a pesos enteros y la utilidad (precio − costo) a 2 decimales, ambos con
/// <see cref="MidpointRounding.ToEven"/> sobre <see cref="decimal"/> (ADR-010 §6): un empate exacto de medio peso va al par
/// (55 con markup 0.1 = 60.5 → 60; 6349 con margen 0.44 = 11337.5 → 11338).
/// </summary>
public static class clsCalculadoraPrecio
{
    /// <summary>Calcula el precio o devuelve <c>PRECIO_INVALIDO</c>.</summary>
    /// <param name="costo">Costo en pesos (&gt; 0).</param>
    /// <param name="margen">Margen como fracción decimal (0.135 = 13,5 %).</param>
    /// <param name="metodo"><c>margen</c> o <c>markup</c> (sin distinguir mayúsculas); nulo o vacío equivale a <c>margen</c>, el valor por defecto de la herramienta.</param>
    public static clsResultado<clsPrecioCalculado> Calcular(decimal costo, decimal margen, string? metodo = clsReglaMargen.MetodoMargen)
    {
        if (costo <= 0m)
        {
            return Invalido($"El costo debe ser positivo (recibido: {Texto(costo)}).");
        }

        if (margen < 0m)
        {
            return Invalido($"El margen no puede ser negativo (recibido: {Texto(margen)}).");
        }

        var metodoNormalizado = string.IsNullOrWhiteSpace(metodo) ? clsReglaMargen.MetodoMargen : metodo.Trim().ToLowerInvariant();
        if (metodoNormalizado is not (clsReglaMargen.MetodoMargen or clsReglaMargen.MetodoMarkup))
        {
            return Invalido($"Método desconocido: '{metodo}'. Use 'margen' o 'markup'.");
        }

        if (metodoNormalizado == clsReglaMargen.MetodoMargen && margen >= 1m)
        {
            return Invalido($"Con método 'margen' el margen debe ser < 1 (recibido: {Texto(margen)}).");
        }

        decimal precio;
        try
        {
            var bruto = metodoNormalizado == clsReglaMargen.MetodoMargen
                ? costo / (1m - margen)
                : costo * (1m + margen);
            precio = Math.Round(bruto, 0, MidpointRounding.ToEven);
        }
        catch (OverflowException)
        {
            return Invalido($"El precio resultante excede el rango numérico (costo: {Texto(costo)}, margen: {Texto(margen)}).");
        }

        var utilidad = Math.Round(precio - costo, 2, MidpointRounding.ToEven);
        return new clsPrecioCalculado(precio, costo, margen, metodoNormalizado, utilidad);
    }

    private static clsErrorDominio Invalido(string mensaje) => new(clsCodigosError.PrecioInvalido, mensaje);

    private static string Texto(decimal valor) => valor.ToString(CultureInfo.InvariantCulture);
}
