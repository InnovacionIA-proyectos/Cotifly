using System.Globalization;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Formato de dinero del PDF y del correo, idéntico al <c>money()</c> del Python en pesos: prefijo <c>$</c> pegado al número,
/// separador de miles <c>.</c> y sin decimales (<c>3294798</c> → <c>$3.294.798</c>). En dólares (HU-012.3) el prefijo es <c>US$</c> y
/// lleva 2 decimales con coma (<c>996.35</c> → <c>US$996,35</c>): el precio en USD se convierte con la TRM a centavos.
/// </summary>
/// <remarks>
/// No depende de ICU ni de la cultura del proceso: <c>ToString("C0", es-CO)</c> con ICU da <c>$ 3.294.798</c> (con espacio,
/// docs/03-infraestructura.md, §3.1) y sin ICU cambiaría otra vez. Aquí se agrupa con la cultura invariante (datos fijos del runtime) y se cambia
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

        if (string.Equals(moneda?.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
        {
            return PrefijoDolares + ConDecimales(monto);
        }

        var entero = Math.Round(monto, 0, MidpointRounding.ToEven);
        return PrefijoPesos + entero.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.');
    }

    /// <summary>
    /// HU-045.3 (#265, B1, decisión del usuario): como <see cref="Formatear"/>, pero en pesos un monto con centavos (el seguro de la mercancía
    /// se guarda en centavos, y con él el precio de la ruta y los totales) se muestra con sus 2 decimales (<c>100.40</c> → <c>$100,40</c>), para
    /// que lo impreso cuadre exacto con lo guardado. Sin centavos, igual que <see cref="Formatear"/> (<c>$1.250.000</c>).
    /// </summary>
    public static string FormatearExacto(decimal? valor, string? moneda = null) =>
        valor is { } monto && !string.Equals(moneda?.Trim(), "USD", StringComparison.OrdinalIgnoreCase) && TieneCentavos(monto)
            ? PrefijoPesos + ConDecimales(monto)
            : Formatear(valor, moneda);

    /// <summary>¿El monto tiene centavos (a 2 decimales)?</summary>
    public static bool TieneCentavos(decimal monto) => Math.Round(monto, 2, MidpointRounding.ToEven) % 1m != 0m;

    /// <summary>
    /// TRM (pesos por dólar) con su escala real, sin rellenar ni recortar (hasta 4 decimales, la de <c>trmAplicada</c>): <c>3306.86</c> →
    /// <c>$3.306,86</c>, <c>3306.8612</c> → <c>$3.306,8612</c>, <c>950</c> → <c>$950</c> (HU-012.3).
    /// </summary>
    public static string FormatearTrm(decimal trm)
    {
        var texto = trm.ToString("#,0.####", CultureInfo.InvariantCulture);
        var punto = texto.LastIndexOf('.');
        return PrefijoPesos + (punto < 0 ? texto.Replace(',', '.') : texto[..punto].Replace(',', '.') + ',' + texto[(punto + 1)..]);
    }

    /// <summary>
    /// TRM de la nota del PDF en USD (HU-012.3, requisito de negocio del 2026-10-05): siempre 2 decimales (half-to-even), miles con <c>.</c> y
    /// decimales con <c>,</c>: <c>3273.49</c> → <c>$3.273,49</c>, <c>3306.8612</c> → <c>$3.306,86</c>, <c>950</c> → <c>$950,00</c>.
    /// </summary>
    public static string FormatearTrmNota(decimal trm) => PrefijoPesos + ConDecimales(trm);

    /// <summary>Miles con <c>.</c> y 2 decimales con <c>,</c> (half-to-even), sin depender de la cultura del proceso.</summary>
    private static string ConDecimales(decimal monto)
    {
        var texto = Math.Round(monto, 2, MidpointRounding.ToEven).ToString("#,0.00", CultureInfo.InvariantCulture);
        var punto = texto.LastIndexOf('.');
        return texto[..punto].Replace(',', '.') + ',' + texto[(punto + 1)..];
    }
}
