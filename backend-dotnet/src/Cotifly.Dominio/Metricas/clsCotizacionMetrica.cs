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

    /// <summary>
    /// Costo en COP de la versión vigente (nunca se convierte): la suma del <c>costoFinal</c> de sus rutas, <c>null</c> si alguna no lo
    /// tiene (HU-021).
    /// </summary>
    public decimal? Costo { get; init; }

    /// <summary>Precio de la versión vigente en su moneda (la cabecera: suma del <c>precioFinalLinea</c> de sus rutas).</summary>
    public decimal? PrecioFinal { get; init; }

    public string? Moneda { get; init; }

    /// <summary>TRM con que se cotizó en USD; 0 en COP.</summary>
    public decimal TrmAplicada { get; init; }

    /// <summary>Margen de la cotización: el de sus rutas ponderado por <c>costoFinal</c> (sin costo, el promedio simple).</summary>
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

    /// <summary>
    /// Rutas de la versión vigente (HU-021.10, decisión 8), ordenadas por <c>orden</c>: de ellas salen el margen ponderado y la apertura
    /// por línea de negocio. <see cref="Costo"/> y <see cref="MargenAplicado"/> ya las resumen para la cotización.
    /// </summary>
    public IReadOnlyList<clsRutaMetrica> Rutas { get; init; } = [];

    /// <summary><c>true</c> si la moneda es COP o no se informó (el esquema la pone en COP por defecto).</summary>
    public bool EsPesos => string.IsNullOrWhiteSpace(Moneda) || string.Equals(Moneda.Trim(), MonedaPesos, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>valor_cop</c> del tablero: el precio tal cual en pesos; en otra moneda <c>round(precioFinal × trmAplicada, 2)</c>
    /// (half-to-even). Sin precio, o en otra moneda sin TRM, es <c>null</c> (no suma dinero). No es el <c>precio_cop</c> de ADR-014
    /// (precio en pesos recalculado del costo y el margen antes de convertir): puede diferir unos pesos.
    /// </summary>
    public decimal? ValorCop => ACop(PrecioFinal);

    /// <summary>
    /// <paramref name="precio"/> (de la cotización o de una de sus rutas, en la moneda de la cotización) en COP con la regla de
    /// <see cref="ValorCop"/>. Una ruta en USD se redondea por sí sola, así que la suma de las rutas puede diferir unos centavos del
    /// <see cref="ValorCop"/> de la cabecera.
    /// </summary>
    public decimal? ACop(decimal? precio) => precio switch
    {
        null => null,
        { } valor when EsPesos => valor,
        { } valor when TrmAplicada > 0m => Math.Round(valor * TrmAplicada, 2, MidpointRounding.ToEven),
        _ => null,
    };
}
