using System.Globalization;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Precio;

/// <summary>
/// Monedas de la cotización y conversión del precio (ADR-014). El costeo y el precio se calculan en pesos
/// (<see cref="clsCalculadoraPrecio"/>, a pesos enteros); en dólares el precio es <c>precio en pesos / TRM</c> redondeado a
/// <see cref="DecimalesDolares"/> decimales con <see cref="MidpointRounding.ToEven"/> sobre <see cref="decimal"/>. La TRM la aporta la
/// fuente oficial, nunca el LLM.
/// </summary>
public static class clsConversorMoneda
{
    public const string Pesos = "COP";
    public const string Dolares = "USD";

    /// <summary>Decimales del precio en dólares (centavos).</summary>
    public const int DecimalesDolares = 2;

    /// <summary>Monedas en que se puede cotizar.</summary>
    public static IReadOnlyList<string> Soportadas { get; } = [Pesos, Dolares];

    /// <summary>
    /// Moneda normalizada (sin espacios y en mayúsculas); nula o vacía → <see cref="Pesos"/>. Una moneda que no está en
    /// <see cref="Soportadas"/> devuelve <c>PRECIO_INVALIDO</c>.
    /// </summary>
    public static clsResultado<string> Normalizar(string? moneda)
    {
        var normalizada = string.IsNullOrWhiteSpace(moneda) ? Pesos : moneda.Trim().ToUpperInvariant();
        return Soportadas.Contains(normalizada)
            ? normalizada
            : new clsErrorDominio(clsCodigosError.PrecioInvalido, $"Moneda no soportada: '{moneda?.Trim()}'. Use COP o USD.");
    }

    /// <summary>¿La moneda (ya normalizada o no) es pesos? Nula o vacía cuenta como pesos.</summary>
    public static bool EsPesos(string? moneda) => string.IsNullOrWhiteSpace(moneda) || string.Equals(moneda.Trim(), Pesos, StringComparison.OrdinalIgnoreCase);

    /// <summary>Decimales con que se expresa un precio en la moneda: 0 en pesos, 2 en dólares.</summary>
    public static int Decimales(string? moneda) => EsPesos(moneda) ? 0 : DecimalesDolares;

    /// <summary>Redondea un monto a los decimales de su moneda (half-to-even).</summary>
    public static decimal Redondear(decimal monto, string? moneda) => Math.Round(monto, Decimales(moneda), MidpointRounding.ToEven);

    /// <summary>
    /// Convierte un precio en pesos a dólares con la TRM (COP por USD): <c>round(precioPesos / trm, 2)</c> half-to-even.
    /// TRM ≤ 0 o precio negativo → <c>PRECIO_INVALIDO</c>.
    /// </summary>
    public static clsResultado<decimal> PesosADolares(decimal precioPesos, decimal trm)
    {
        if (trm <= 0m)
        {
            return new clsErrorDominio(clsCodigosError.PrecioInvalido, $"La TRM debe ser positiva (recibida: {Texto(trm)}).");
        }

        if (precioPesos < 0m)
        {
            return new clsErrorDominio(clsCodigosError.PrecioInvalido, $"El precio en pesos no puede ser negativo (recibido: {Texto(precioPesos)}).");
        }

        return Math.Round(precioPesos / trm, DecimalesDolares, MidpointRounding.ToEven);
    }

    private static string Texto(decimal valor) => valor.ToString(CultureInfo.InvariantCulture);
}
