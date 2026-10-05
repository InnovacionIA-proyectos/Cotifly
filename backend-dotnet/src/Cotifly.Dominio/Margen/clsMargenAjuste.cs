using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Ajuste (negociación) del margen frente a la política (<c>dbo_cotifly.MargenAjustes</c>).
/// Los márgenes se redondean a 4 decimales half-to-even (escala de las columnas <c>DECIMAL(9,4)</c>) antes de
/// validar y de calcular <see cref="Diferencia"/> = aplicado − política, para que lo que queda en memoria sea
/// exactamente lo que guarda la base (el Python redondeaba la diferencia a 6, pero la columna solo guarda 4).
/// </summary>
public sealed class clsMargenAjuste
{
    public const string CodigoError = clsCodigosError.AjusteMargenInvalido;

    /// <summary>Decimales de los márgenes y de la diferencia (escala de <c>MargenAjustes</c>).</summary>
    public const int Decimales = 4;

    private clsMargenAjuste()
    {
    }

    public Guid Id { get; private set; }
    public Guid? CotizacionId { get; private set; }
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
        string? metodoCalculo = clsReglaMargen.MetodoMargen)
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
            Folio = clsValidacion.Normalizar(folio),
            MargenPolitica = politica,
            MargenAplicado = aplicado,
            Diferencia = aplicado - politica,
            Motivo = motivo.Trim(),
            UsuarioId = usuarioId,
        };
    }

    /// <summary>
    /// Justificación lista para guardar (en <c>MargenAjustes.motivo</c> y en la auditoría): sin caracteres de control (saltos de línea,
    /// tabuladores, nulos), que se reemplazan por un espacio, y sin espacios en los extremos; <c>null</c> si queda vacía.
    /// </summary>
    public static string? LimpiarJustificacion(string? justificacion)
    {
        if (justificacion is null)
        {
            return null;
        }

        var limpia = new string([.. justificacion.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return limpia.Length == 0 ? null : limpia;
    }

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
        string? metodoCalculo = clsReglaMargen.MetodoMargen)
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
            Folio = clsValidacion.Normalizar(folio),
            MargenPolitica = sugerido,
            MargenAplicado = aplicado,
            Diferencia = aplicado - sugerido,
            Motivo = clsValidacion.Normalizar(justificacion),
            UsuarioId = usuarioId,
        };
    }
}
