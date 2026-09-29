namespace Cotifly.Dominio.Metricas;

/// <summary>
/// Una fila de <c>Cotizaciones</c> vista por el tablero de métricas (HU-010.1, ADR-016): solo los campos que entran en algún
/// indicador, más si es la versión vigente de su cadena y el resumen de sus ajustes de margen.
/// </summary>
public sealed record clsCotizacionMetrica
{
    public const string MonedaPesos = "COP";

    public required Guid UsuarioId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// <c>true</c> si es la de mayor <c>version</c> de su cadena <c>(usuarioId, folio base)</c>. Solo las vigentes forman el
    /// universo de los indicadores; las demás cuentan únicamente en <see cref="clsIndicadoresMetricas.Versiones"/>.
    /// </summary>
    public bool EsVigente { get; init; } = true;

    /// <summary>Costo en COP (nunca se convierte).</summary>
    public decimal? Costo { get; init; }

    /// <summary>Precio en la moneda de la cotización.</summary>
    public decimal? PrecioFinal { get; init; }

    public string? Moneda { get; init; }

    /// <summary>TRM con que se cotizó en USD; 0 en COP.</summary>
    public decimal TrmAplicada { get; init; }

    public decimal? MargenAplicado { get; init; }

    public bool? Ganada { get; init; }

    public DateTimeOffset? CosteoListoAt { get; init; }

    public string? Origen { get; init; }

    public string? Destino { get; init; }

    /// <summary>Ajustes de margen (<c>MargenAjustes</c>) de la cotización.</summary>
    public int NumAjustes { get; init; }

    /// <summary>Ajustes con <c>diferencia</c> no nula (denominador del promedio de la desviación).</summary>
    public int NumDiferencias { get; init; }

    /// <summary>Suma de <c>MargenAjustes.diferencia</c> de la cotización.</summary>
    public decimal? SumaDiferencias { get; init; }

    /// <summary><c>true</c> si la moneda es COP o no se informó (el esquema la pone en COP por defecto).</summary>
    public bool EsPesos => string.IsNullOrWhiteSpace(Moneda) || string.Equals(Moneda.Trim(), MonedaPesos, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Precio en COP: el precio tal cual en pesos; en otra moneda <c>round(precioFinal × trmAplicada, 2)</c> (half-to-even). Sin
    /// precio, o en otra moneda sin TRM, es <c>null</c> (no suma dinero).
    /// </summary>
    public decimal? PrecioCop => PrecioFinal switch
    {
        null => null,
        { } precio when EsPesos => precio,
        { } precio when TrmAplicada > 0m => Math.Round(precio * TrmAplicada, 2, MidpointRounding.ToEven),
        _ => null,
    };
}
