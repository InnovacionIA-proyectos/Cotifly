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

    /// <summary>
    /// B3 (#173): la versión de la cadena que tiene el resultado (<see cref="Ganada"/>), con su precio, moneda, TRM y costo. Si la v1 se
    /// marcó ganada después de crear la <c>-v2</c>, el valor ganado es el de la v1, no el de la vigente (que puede no tener precio).
    /// <c>null</c> = la misma vigente.
    /// </summary>
    public clsVersionResultadoMetrica? VersionResultado { get; init; }

    /// <summary>Valor en COP de la versión con el resultado (<see cref="VersionResultado"/>, o la vigente): base de lo ganado.</summary>
    public decimal? ValorResultadoCop => VersionResultado is { } v ? ACop(v.PrecioFinal, v.Moneda, v.TrmAplicada) : ValorCop;

    /// <summary>Costo en COP de la versión con el resultado (<see cref="VersionResultado"/>, o la vigente).</summary>
    public decimal? CostoResultado => VersionResultado is { } v ? v.Costo : Costo;

    /// <summary>
    /// B3 (#173): rutas de la versión que tiene el resultado cuando NO es la vigente; <c>null</c> = las de la vigente
    /// (<see cref="Rutas"/>). La apertura por línea de negocio saca de aquí lo ganado.
    /// </summary>
    public IReadOnlyList<clsRutaMetrica>? RutasResultado { get; init; }

    /// <summary>Rutas de la versión con el resultado (<see cref="RutasResultado"/> o, si es la vigente, <see cref="Rutas"/>).</summary>
    public IReadOnlyList<clsRutaMetrica> RutasDelResultado => RutasResultado ?? Rutas;

    /// <summary>Precio de una ruta de la versión con el resultado, en COP con la moneda y la TRM de esa versión.</summary>
    public decimal? ACopResultado(decimal? precio) =>
        VersionResultado is { } v ? ACop(precio, v.Moneda, v.TrmAplicada) : ACop(precio);

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
    public decimal? ACop(decimal? precio) => ACop(precio, Moneda, TrmAplicada);

    private static decimal? ACop(decimal? precio, string? moneda, decimal trm) => precio switch
    {
        null => null,
        { } valor when string.IsNullOrWhiteSpace(moneda) || string.Equals(moneda.Trim(), MonedaPesos, StringComparison.OrdinalIgnoreCase) => valor,
        { } valor when trm > 0m => Math.Round(valor * trm, 2, MidpointRounding.ToEven),
        _ => null,
    };
}

/// <summary>B3 (#173): precio, moneda, TRM y costo de la versión de la cadena que tiene el resultado.</summary>
public sealed record clsVersionResultadoMetrica(decimal? PrecioFinal, string? Moneda, decimal TrmAplicada, decimal? Costo);
