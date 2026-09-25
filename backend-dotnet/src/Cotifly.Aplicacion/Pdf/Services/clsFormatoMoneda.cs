using System.Globalization;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Formato de dinero del PDF y del correo, idéntico al <c>money()</c> del Python: prefijo <c>$</c> (o <c>US$</c> para USD)
/// pegado al número, separador de miles <c>.</c> y sin decimales (<c>3294798</c> → <c>$3.294.798</c>).
/// </summary>
/// <remarks>
/// No depende de ICU ni de la cultura del proceso: <c>ToString("C0", es-CO)</c> con ICU da <c>$ 3.294.798</c> (con espacio,
/// README §3) y sin ICU cambiaría otra vez. Aquí se agrupa con la cultura invariante (datos fijos del runtime) y se cambia
/// la coma por punto. Redondeo a pesos half-to-even sobre <c>decimal</c> (el <c>round</c> del Python, ADR-010 §6).
/// </remarks>
public static class clsFormatoMoneda
{
    /// <summary>Texto para un valor ausente (el <c>"—"</c> del Python).</summary>
    public const string SinValor = "—";

    public const string PrefijoPesos = "$";
    public const string PrefijoDolares = "US$";

    /// <summary>Formatea el valor; <c>null</c> → <see cref="SinValor"/>. Los negativos quedan como en el Python (<c>$-1.000</c>).</summary>
    public static string Formatear(decimal? valor, string? moneda = null)
    {
        if (valor is not { } monto)
        {
            return SinValor;
        }

        var prefijo = string.Equals(moneda?.Trim(), "USD", StringComparison.OrdinalIgnoreCase) ? PrefijoDolares : PrefijoPesos;
        var entero = Math.Round(monto, 0, MidpointRounding.ToEven);
        return prefijo + entero.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');
    }
}
