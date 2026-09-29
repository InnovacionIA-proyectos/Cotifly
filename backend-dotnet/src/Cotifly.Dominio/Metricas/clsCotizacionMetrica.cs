namespace Cotifly.Dominio.Metricas;

/// <summary>
/// Una cotización vista por el tablero de métricas (HU-010.1, ADR-016 §2): una fila por <b>cadena de versiones</b>
/// <c>(usuarioId, folio base)</c>. Los valores (precio, costo, margen, moneda, costeo, ciudades, deadline, ajustes) son los de la
/// versión vigente (la de mayor <c>version</c>); la fecha del periodo es la primera creación de la cadena y el resultado es el
/// más reciente de toda la cadena.
/// </summary>
public sealed record clsCotizacionMetrica
{
    public const string MonedaPesos = "COP";

    public required Guid UsuarioId { get; init; }

    /// <summary>
    /// Primera creación de la cadena (<c>MIN(createdAt)</c> de todas sus versiones): decide el periodo y el mes de la serie, así
    /// que rehacer una cotización no la mueve de mes ni cambia periodos pasados.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary><c>createdAt</c> de la versión vigente (base del tiempo de cotización con <see cref="CosteoListoAt"/>).</summary>
    public DateTimeOffset? CreatedAtVigente { get; init; }

    /// <summary>Filas (versiones) de la cadena.</summary>
    public int Versiones { get; init; } = 1;

    /// <summary>Costo en COP de la versión vigente (nunca se convierte).</summary>
    public decimal? Costo { get; init; }

    /// <summary>Precio de la versión vigente en su moneda.</summary>
    public decimal? PrecioFinal { get; init; }

    public string? Moneda { get; init; }

    /// <summary>TRM con que se cotizó en USD; 0 en COP.</summary>
    public decimal TrmAplicada { get; init; }

    public decimal? MargenAplicado { get; init; }

    /// <summary>
    /// Resultado de la cadena: el último no nulo de cualquiera de sus versiones (mayor <c>marcadoAt</c>; a igualdad o sin fecha,
    /// la mayor <c>version</c>). Un "ganada" marcado en la v1 no se pierde porque exista una v2 sin marcar.
    /// </summary>
    public bool? Ganada { get; init; }

    public DateTimeOffset? CosteoListoAt { get; init; }

    public string? Origen { get; init; }

    public string? Destino { get; init; }

    /// <summary>Fecha límite de la solicitud (script 0004); <c>null</c> si no tiene o si la columna no existe.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Ajustes de margen (<c>MargenAjustes</c>) de la versión vigente.</summary>
    public int NumAjustes { get; init; }

    /// <summary>Ajustes con <c>diferencia</c> no nula (denominador de los promedios de desviación).</summary>
    public int NumDiferencias { get; init; }

    /// <summary>Suma de <c>MargenAjustes.diferencia</c>.</summary>
    public decimal? SumaDiferencias { get; init; }

    /// <summary>Suma de <c>ABS(MargenAjustes.diferencia)</c>.</summary>
    public decimal? SumaDiferenciasAbsolutas { get; init; }

    /// <summary><c>true</c> si la moneda es COP o no se informó (el esquema la pone en COP por defecto).</summary>
    public bool EsPesos => string.IsNullOrWhiteSpace(Moneda) || string.Equals(Moneda.Trim(), MonedaPesos, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>valor_cop</c> del tablero: el precio tal cual en pesos; en otra moneda <c>round(precioFinal × trmAplicada, 2)</c>
    /// (half-to-even). Sin precio, o en otra moneda sin TRM, es <c>null</c> (no suma dinero). No es el <c>precio_cop</c> de ADR-014
    /// (precio en pesos recalculado del costo y el margen antes de convertir, el piso del PDF): puede diferir unos pesos.
    /// </summary>
    public decimal? ValorCop => PrecioFinal switch
    {
        null => null,
        { } precio when EsPesos => precio,
        { } precio when TrmAplicada > 0m => Math.Round(precio * TrmAplicada, 2, MidpointRounding.ToEven),
        _ => null,
    };
}
