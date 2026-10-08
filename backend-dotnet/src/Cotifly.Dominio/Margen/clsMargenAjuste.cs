using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Ajuste (negociación) del margen frente a la política (<c>dbo_cotifly.MargenAjustes</c>).
/// Los márgenes se redondean a 4 decimales half-to-even (escala de las columnas <c>DECIMAL(9,4)</c>) antes de
/// validar y de calcular <see cref="Diferencia"/> = aplicado − política, para que lo que queda en memoria sea
/// exactamente lo que guarda la base (el Python redondeaba la diferencia a 6, pero la columna solo guarda 4).
/// </summary>
public sealed partial class clsMargenAjuste
{
    public const string CodigoError = clsCodigosError.AjusteMargenInvalido;

    /// <summary>Decimales de los márgenes y de la diferencia (escala de <c>MargenAjustes</c>).</summary>
    public const int Decimales = 4;

    private clsMargenAjuste()
    {
    }

    public Guid Id { get; private set; }
    public Guid? CotizacionId { get; private set; }

    /// <summary>Ruta a la que se le ajustó el margen (<c>lineaSolicitudId</c>, script 0009, HU-021); <c>null</c> en el ajuste del chat.</summary>
    public Guid? LineaSolicitudId { get; private set; }
    public string? Folio { get; private set; }
    public decimal? MargenPolitica { get; private set; }
    public decimal? MargenAplicado { get; private set; }
    public decimal? Diferencia { get; private set; }
    public string? Motivo { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Margen con la escala de la columna: 4 decimales half-to-even (0.123456 → 0.1235).</summary>
    public static decimal RedondearMargen(decimal margen) => Math.Round(margen, Decimales, MidpointRounding.ToEven);

    /// <summary>
    /// Registra un ajuste. El motivo es obligatorio y los márgenes (ya redondeados a 4 decimales) se validan según
    /// el método de cálculo (<see cref="clsReglaMargen"/>): con <c>margen</c> deben estar en [0, 1); con
    /// <c>markup</c> basta m ≥ 0.
    /// </summary>
    public static clsResultado<clsMargenAjuste> Registrar(
        decimal margenPolitica,
        decimal margenAplicado,
        string motivo,
        Guid? usuarioId = null,
        Guid? cotizacionId = null,
        string? folio = null,
        string? metodoCalculo = clsReglaMargen.MetodoMargen,
        Guid? lineaSolicitudId = null)
    {
        var politica = RedondearMargen(margenPolitica);
        var aplicado = RedondearMargen(margenAplicado);
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "motivo", motivo),
            clsReglaMargen.Validar(CodigoError, "margenPolitica", politica, metodoCalculo),
            clsReglaMargen.Validar(CodigoError, "margenAplicado", aplicado, metodoCalculo));

        if (error is not null)
        {
            return error;
        }

        return new clsMargenAjuste
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            LineaSolicitudId = lineaSolicitudId,
            Folio = clsValidacion.Normalizar(folio),
            MargenPolitica = politica,
            MargenAplicado = aplicado,
            Diferencia = aplicado - politica,
            Motivo = motivo.Trim(),
            UsuarioId = usuarioId,
        };
    }

    /// <summary>
    /// Justificación lista para guardar (en <c>MargenAjustes.motivo</c> y en la auditoría): sin secuencias de escape ANSI completas
    /// (<c>ESC [ ... letra</c>, <c>ESC ] ... BEL</c> y <c>ESC</c> más un carácter), sin caracteres de control (saltos de línea,
    /// tabuladores, nulos), que se reemplazan por un espacio, con los espacios repetidos colapsados en uno y sin espacios en los
    /// extremos; <c>null</c> si queda vacía.
    /// </summary>
    public static string? LimpiarJustificacion(string? justificacion)
    {
        if (justificacion is null)
        {
            return null;
        }

        var sinAnsi = ExpresionAnsi().Replace(justificacion, " ");
        var sinControl = new string([.. sinAnsi.Select(c => char.IsControl(c) ? ' ' : c)]);
        var limpia = ExpresionEspacios().Replace(sinControl, " ").Trim();
        return limpia.Length == 0 ? null : limpia;
    }

    /// <summary>CSI (<c>ESC [</c> parámetros y una letra final), OSC (<c>ESC ]</c> hasta BEL o <c>ESC \</c>) o ESC con un carácter.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ExpresionAnsi();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s{2,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ExpresionEspacios();

    /// <summary>
    /// Ajuste del comercial sobre el costeo de Nexo (HU-013.2, ADR-018 §3): <paramref name="margenSugerido"/> queda en
    /// <c>margenPolitica</c> y la justificación en <c>motivo</c>. La justificación es obligatoria solo si el margen queda por debajo del
    /// sugerido (<c>JUSTIFICACION_REQUERIDA</c>); por encima puede faltar (<c>motivo</c> nulo). Mismo redondeo y validación por método
    /// que <see cref="Registrar"/>.
    /// </summary>
    public static clsResultado<clsMargenAjuste> RegistrarNegociacionCosteo(
        decimal margenSugerido,
        decimal margenAplicado,
        string? justificacion,
        Guid usuarioId,
        Guid cotizacionId,
        string folio,
        string? metodoCalculo = clsReglaMargen.MetodoMargen,
        Guid? lineaSolicitudId = null)
    {
        var sugerido = RedondearMargen(margenSugerido);
        var aplicado = RedondearMargen(margenAplicado);
        justificacion = LimpiarJustificacion(justificacion);
        var error = clsValidacion.Primero(
            clsReglaMargen.Validar(CodigoError, "margenPolitica", sugerido, metodoCalculo),
            clsReglaMargen.Validar(CodigoError, "margenAplicado", aplicado, metodoCalculo),
            aplicado < sugerido && string.IsNullOrWhiteSpace(justificacion)
                ? new clsErrorDominio(clsCodigosError.JustificacionRequerida, "El margen queda por debajo del sugerido: la justificación es obligatoria.")
                : null);
        if (error is not null)
        {
            return error;
        }

        return new clsMargenAjuste
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            LineaSolicitudId = lineaSolicitudId,
            Folio = clsValidacion.Normalizar(folio),
            MargenPolitica = sugerido,
            MargenAplicado = aplicado,
            Diferencia = aplicado - sugerido,
            Motivo = clsValidacion.Normalizar(justificacion),
            UsuarioId = usuarioId,
        };
    }
}
