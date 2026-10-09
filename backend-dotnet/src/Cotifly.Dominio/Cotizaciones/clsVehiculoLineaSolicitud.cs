namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Camión de una ruta (<c>dbo_cotifly.CotizacionVehiculosLineaSolicitud</c>, script 0009, HU-021, ADR-019): una fila por camión. Nexo
/// cotiza cada camión como una línea de su solicitud, así que aquí viven el id de esa línea, su estado y sus costos. El levante es una
/// copia del <see cref="clsLineaSolicitud.ValorLevanteContenedor"/> de su ruta (decisión 4 del 2026-10-08): cada camión paga el suyo.
/// Vehículo y carrocería vacíos = "sin selección automática" del cubicador; la Mesa de Costeo de Nexo los resuelve
/// (<see cref="AsignarVehiculo"/>). Los costos son internos (de Nexo): nunca van al comercial. <see cref="CreatedAt"/> y
/// <see cref="UpdatedAt"/> los asigna la persistencia.
/// </summary>
public sealed class clsVehiculoLineaSolicitud
{
    private clsVehiculoLineaSolicitud()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Ruta a la que pertenece (<c>lineaSolicitudId</c>).</summary>
    public Guid LineaSolicitudId { get; private set; }

    /// <summary>Fila de la Tabla 7 del cubicador de la que salió el camión; <c>null</c> si no salió de una recomendación.</summary>
    public Guid? IdRecomendacionVehiculo { get; private set; }

    /// <summary>Código del vehículo de Nexo (<c>3S3</c>, <c>2S2</c>...); <c>null</c> = sin selección automática.</summary>
    public string? TipoVehiculo { get; private set; }

    public string? Carroceria { get; private set; }

    /// <summary>Peso que lleva el camión (es el que viaja a Nexo).</summary>
    public decimal? PesoKg { get; private set; }

    /// <summary>Flete del camión que devolvió Nexo; <c>null</c> mientras no lo costea.</summary>
    public decimal? CostoFlete { get; private set; }

    /// <summary>Retorno del contenedor que devolvió Nexo (0 si Nexo no lo cobra); <c>null</c> mientras no lo costea.</summary>
    public decimal? CostoRetornoContenedor { get; private set; }

    /// <summary>Levante del contenedor: copia del valor que digitó el comercial en la ruta (0 si no aplica).</summary>
    public decimal? CostoLevanteContenedor { get; private set; }

    /// <summary>Id de la línea de la solicitud de Nexo que corresponde a este camión (<c>solicitud.lineas[].id</c>).</summary>
    public string? LineaSolicitudNexoId { get; private set; }

    /// <summary>Estado del costeo de este camión en Nexo; <c>null</c> mientras no se envía.</summary>
    public EstadoCosteo? EstadoCosteo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Posición del camión en su ruta al crearlo (1 = el primero; la fila de la recomendación del cubicador, HU-021.5). No es una
    /// columna (el esquema no tiene orden de camión, deuda para un script ≥ 0011): sirve para ordenar los camiones nuevos antes de
    /// guardarlos y la persistencia la convierte en un desfase determinista de <see cref="CreatedAt"/> (<see cref="DesfaseCreacion"/>), así
    /// que leídos de la base quedan en el mismo orden. Leído de la base vale 0.
    /// </summary>
    public int OrdenCreacion { get; private set; }

    /// <summary>Desfase de <see cref="CreatedAt"/> por cada posición de <see cref="OrdenCreacion"/> (1 ms: lo conserva <c>datetimeoffset</c>).</summary>
    public static readonly TimeSpan DesfaseCreacion = TimeSpan.FromMilliseconds(1);

    /// <summary>¿Nexo ya le puso flete? (<c>autocosteo</c> o <c>entregada</c> con flete.)</summary>
    public bool TieneCosteo => CostoFlete is not null && EstadoCosteo is { } estado && clsDerivacionEstadoCosteo.TieneCosteo(estado);

    /// <summary>Costo total del camión (flete + retorno + levante), o <c>null</c> sin flete.</summary>
    public decimal? CostoTotal => CostoFlete is { } flete ? flete + (CostoRetornoContenedor ?? 0m) + (CostoLevanteContenedor ?? 0m) : null;

    /// <summary>
    /// Camión nuevo de una ruta. <paramref name="levante"/> es el valor del levante de la ruta (decisión 4: se copia a cada camión; 0
    /// si no aplica). Vehículo y carrocería pueden venir vacíos (sin selección automática del cubicador).
    /// </summary>
    internal static clsVehiculoLineaSolicitud Crear(
        Guid lineaSolicitudId, string? tipoVehiculo, string? carroceria, decimal? pesoKg, decimal? levante, Guid? idRecomendacionVehiculo,
        int ordenCreacion = 0)
    {
        if (pesoKg is < 0m || levante is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(pesoKg), "El peso y el levante del camión no pueden ser negativos.");
        }

        return new clsVehiculoLineaSolicitud
        {
            Id = Guid.NewGuid(),
            LineaSolicitudId = lineaSolicitudId,
            IdRecomendacionVehiculo = idRecomendacionVehiculo,
            TipoVehiculo = Normalizar(tipoVehiculo),
            Carroceria = Normalizar(carroceria),
            PesoKg = pesoKg,
            CostoLevanteContenedor = levante ?? 0m,
            OrdenCreacion = ordenCreacion,
        };
    }

    /// <summary>Costo del flete de una cotización del chat (sin Nexo): sin estado de costeo, sin retorno.</summary>
    internal void RegistrarCostoChat(decimal flete)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(flete);
        CostoFlete = flete;
        CostoRetornoContenedor = 0m;
    }

    /// <summary>El levante de la ruta cambió (re-solicitud con cambios de carga): se vuelve a copiar (decisión 4).</summary>
    internal void CopiarLevante(decimal? levante)
    {
        if (levante is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(levante), "El levante no puede ser negativo.");
        }

        CostoLevanteContenedor = levante ?? 0m;
    }

    /// <summary>Cambia vehículo, carrocería y peso (re-solicitud con cambios de carga en una ruta de un camión).</summary>
    internal void CambiarCarga(string? tipoVehiculo, string? carroceria, decimal? pesoKg)
    {
        TipoVehiculo = Normalizar(tipoVehiculo);
        Carroceria = Normalizar(carroceria);
        PesoKg = pesoKg is < 0m ? throw new ArgumentOutOfRangeException(nameof(pesoKg), "El peso no puede ser negativo.") : pesoKg;
    }

    /// <summary>
    /// El vehículo y la carrocería de la línea en Nexo (#134): la Mesa de Costeo se los puso a una línea que iba vacía o los cambió. Lo que
    /// Nexo trae manda; un valor vacío de Nexo no borra el de Cotifly, y la misma sigla con otras mayúsculas no cuenta como cambio.
    /// Devuelve <c>true</c> si cambió.
    /// </summary>
    internal bool AsignarVehiculo(string? tipoVehiculo, string? carroceria)
    {
        var antes = (TipoVehiculo, Carroceria);
        TipoVehiculo = Elegir(TipoVehiculo, tipoVehiculo);
        Carroceria = Elegir(Carroceria, carroceria);
        return antes != (TipoVehiculo, Carroceria);
    }

    /// <summary>
    /// La Mesa de Costeo borró en Nexo la línea de este camión (#134): ya no se va a costear. El camión pierde el id de la línea y los
    /// costos de Nexo (el levante, de Cotifly, se conserva) y queda <see cref="Cotizaciones.EstadoCosteo.Descartada"/>: se ignora en el
    /// estado y en los totales de su ruta, no recibe conexos y el polling no lo vuelve a leer. Devuelve <c>true</c> si cambió.
    /// </summary>
    internal bool DescartarPorLineaBorrada()
    {
        if (LineaSolicitudNexoId is null && EstadoCosteo == Cotizaciones.EstadoCosteo.Descartada)
        {
            return false;
        }

        ReiniciarCosteo();
        EstadoCosteo = Cotizaciones.EstadoCosteo.Descartada;
        return true;
    }

    private static string? Elegir(string? actual, string? deNexo) =>
        Normalizar(deNexo) is { } nuevo && !string.Equals(nuevo, actual, StringComparison.OrdinalIgnoreCase) ? nuevo : actual;

    /// <summary>Nexo recibió la solicitud (o falló el envío): el camión toma el estado de la solicitud.</summary>
    internal bool RegistrarEstado(EstadoCosteo estado)
    {
        var cambio = EstadoCosteo != estado;
        EstadoCosteo = estado;
        return cambio;
    }

    /// <summary>Id de la línea en Nexo, asignado la primera vez que el polling ve la solicitud (se conserva después).</summary>
    internal bool AsignarLineaNexo(string lineaSolicitudNexoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineaSolicitudNexoId);
        var id = lineaSolicitudNexoId.Trim();
        if (LineaSolicitudNexoId == id)
        {
            return false;
        }

        LineaSolicitudNexoId = id;
        return true;
    }

    /// <summary>
    /// Estado y costos del camión en la última consulta del polling (HU-005.6). <paramref name="flete"/> <c>null</c> = sin flete (el
    /// retorno se olvida también). El levante no lo toca: es la copia de la ruta. Devuelve <c>true</c> si algo cambió.
    /// </summary>
    internal bool RegistrarCosteo(EstadoCosteo estado, decimal? flete, decimal? retorno)
    {
        if (flete is < 0m || retorno is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(flete), "El flete y el retorno no pueden ser negativos.");
        }

        decimal? retornoFinal = flete is null ? null : retorno ?? 0m;
        var cambio = EstadoCosteo != estado || CostoFlete != flete || CostoRetornoContenedor != retornoFinal;
        EstadoCosteo = estado;
        CostoFlete = flete;
        CostoRetornoContenedor = retornoFinal;
        return cambio;
    }

    /// <summary>
    /// Re-solicitud en el mismo folio (HU-013.4): el camión vuelve a viajar a Nexo como una línea nueva. Se olvidan el id anterior en
    /// Nexo, el estado y los costos de Nexo (el levante, que es de Cotifly, se conserva).
    /// </summary>
    internal void ReiniciarCosteo()
    {
        LineaSolicitudNexoId = null;
        EstadoCosteo = null;
        CostoFlete = null;
        CostoRetornoContenedor = null;
    }

    private static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
