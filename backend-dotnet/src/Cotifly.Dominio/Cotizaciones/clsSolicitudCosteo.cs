using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Datos del formulario ampliado que viajan a Nexo en <c>POST /api/captura-solicitudes</c> (HU-005.3, HU-005.8): el encabezado
/// de la solicitud y su única línea (una cotización de Cotifly = una solicitud de Nexo con una línea, ADR-015). Los límites son
/// los de <c>CapturaSolicitudRequest</c>/<c>CapturaLineaRequest</c> de Nexo, para que un formulario válido en Cotifly no lo
/// rechace Nexo por tamaño o rango. Los catálogos cerrados (<see cref="TipoVehiculo"/>, <see cref="Carroceria"/>,
/// <see cref="ClaseViaje"/>, <see cref="ClaseContenedor"/>, <see cref="Tipologia"/>) llevan el código de Nexo tal cual.
/// </summary>
public sealed record clsSolicitudCosteo
{
    public const string CodigoError = clsCodigosError.SolicitudCosteoInvalida;
    public const int LargoCiudad = 150;
    public const int LargoCatalogo = 100;
    public const int LargoCorto = 50;
    public const int LargoClaseContenedor = 20;
    public const int LargoReferencia = 200;
    public const int MaximoPosiciones = 200;
    public const decimal MaximoHoras = 240m;
    public const decimal MaximoPeso = 999_999.99m;
    public const decimal MaximoValor = 9_999_999_999_999.99m;

    /// <summary>Tolerancia de Nexo entre el peso de la línea y la suma de las posiciones (1 %, <c>CapturarSolicitudValidador</c>).</summary>
    public const decimal ToleranciaPeso = 0.01m;

    /// <summary>Código de Nexo (va en el detalle): la línea no trae peso &gt; 0 ni posiciones con peso.</summary>
    public const string PesoRequerido = "PESO_REQUERIDO";

    /// <summary>Código de Nexo (va en el detalle): el peso difiere más del 1 % de la suma de las posiciones.</summary>
    public const string PesoInconsistente = "PESO_INCONSISTENTE";

    public required TipoSolicitud Tipo { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public decimal? ValorEstimado { get; init; }
    public string? Referencia { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    public required string Origen { get; init; }
    public required string Destino { get; init; }
    public string? TipoVehiculo { get; init; }
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }

    /// <summary>Posiciones de cubicaje (bultos), en el orden del formulario.</summary>
    public IReadOnlyList<clsPosicionCarga> Posiciones { get; init; } = [];

    /// <summary>Primer error de la solicitud, o <c>null</c> si Nexo la aceptaría por forma y rangos.</summary>
    public clsErrorDominio? Validar()
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "origen", Origen, LargoCiudad),
            clsValidacion.Requerido(CodigoError, "destino", Destino, LargoCiudad),
            clsValidacion.LargoMaximo(CodigoError, "referencia", Referencia, LargoReferencia),
            clsValidacion.LargoMaximo(CodigoError, "tipo_vehiculo", TipoVehiculo, LargoCatalogo),
            clsValidacion.LargoMaximo(CodigoError, "carroceria", Carroceria, LargoCatalogo),
            clsValidacion.LargoMaximo(CodigoError, "tipologia", Tipologia, LargoCorto),
            clsValidacion.LargoMaximo(CodigoError, "clase_viaje", ClaseViaje, LargoCorto),
            clsValidacion.LargoMaximo(CodigoError, "clase_contenedor", ClaseContenedor, LargoClaseContenedor),
            clsValidacion.LargoMaximo(CodigoError, "ciudad_devolucion_contenedor", CiudadDevolucionContenedor, LargoCiudad),
            clsValidacion.LargoMaximo(CodigoError, "ciudad_levante_contenedor", CiudadLevanteContenedor, LargoCiudad),
            Rango("valor_estimado", ValorEstimado, 0m, MaximoValor),
            Rango("horas_cargue", HorasCargue, 0m, MaximoHoras),
            Rango("horas_descargue", HorasDescargue, 0m, MaximoHoras),
            Rango("valor_aprovisionamiento", ValorAprovisionamiento, 0m, MaximoValor),
            Rango("peso_kg", PesoKg, 0m, MaximoPeso),
            Rango("valor_mercancia", ValorMercancia, 0m, MaximoValor),
            Posiciones.Count > MaximoPosiciones
                ? new clsErrorDominio(CodigoError, $"Una solicitud admite máximo {MaximoPosiciones} posiciones.")
                : null);

        return error
            ?? Posiciones.Select((p, i) => p is null
                ? new clsErrorDominio(CodigoError, $"La posición {i + 1} está vacía.")
                : p.Validar(i + 1)).FirstOrDefault(e => e is not null)
            ?? ValidarPeso();
    }

    /// <summary>Suma <c>cantidad × pesoKg</c> de las posiciones con peso (la misma de Nexo).</summary>
    public decimal SumaPesoPosiciones => Posiciones.Where(p => p?.PesoKg is > 0).Sum(p => p.Cantidad * p.PesoKg!.Value);

    /// <summary>
    /// Regla de peso de Nexo (<c>CapturarSolicitudValidador.ValidarLinea</c>): el peso de la línea es <see cref="PesoKg"/> o, si
    /// falta, la suma de las posiciones; debe ser &gt; 0 (<see cref="PesoRequerido"/>) y, si hay posiciones con peso, no diferir de
    /// su suma más del 1 % (<see cref="PesoInconsistente"/>). Se valida antes de guardar para no enviar algo que Nexo rechazará.
    /// </summary>
    private clsErrorDominio? ValidarPeso()
    {
        var suma = SumaPesoPosiciones;
        var peso = PesoKg ?? (suma > 0 ? suma : null);
        if (peso is null or <= 0)
        {
            return new clsErrorDominio(CodigoError,
                $"La línea {Origen} → {Destino} debe traer el peso total en kg (o posiciones con peso).", PesoRequerido);
        }

        if (suma > 0 && Math.Abs(peso.Value - suma) / peso.Value > ToleranciaPeso)
        {
            return new clsErrorDominio(CodigoError,
                $"El peso total ({peso.Value:0.##} kg) no coincide con la suma de las posiciones ({suma:0.##} kg).", PesoInconsistente);
        }

        return null;
    }

    internal static clsErrorDominio? Rango(string campo, decimal? valor, decimal minimo, decimal maximo) =>
        valor is { } v && (v < minimo || v > maximo)
            ? new clsErrorDominio(CodigoError, $"El campo '{campo}' debe estar entre {minimo} y {maximo}.")
            : null;
}

/// <summary>Pieza o bulto a transportar (<c>CapturaPosicionRequest</c> de Nexo), dimensiones en centímetros y peso en kilogramos.</summary>
public sealed record clsPosicionCarga(string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg)
{
    public const int LargoDescripcion = 200;
    public const int CantidadMaxima = 100_000;
    public const decimal MedidaMinima = 0.1m;
    public const decimal MedidaMaxima = 999_999m;

    /// <summary>Primer error de la posición número <paramref name="numero"/> (1 = la primera), o <c>null</c>.</summary>
    public clsErrorDominio? Validar(int numero)
    {
        var codigo = clsSolicitudCosteo.CodigoError;
        return clsValidacion.Primero(
            clsValidacion.LargoMaximo(codigo, $"posiciones[{numero}].descripcion", Descripcion, LargoDescripcion),
            Cantidad is < 1 or > CantidadMaxima
                ? new clsErrorDominio(codigo, $"La cantidad de la posición {numero} debe estar entre 1 y {CantidadMaxima}.")
                : null,
            clsSolicitudCosteo.Rango($"posiciones[{numero}].largo_cm", LargoCm, MedidaMinima, MedidaMaxima),
            clsSolicitudCosteo.Rango($"posiciones[{numero}].ancho_cm", AnchoCm, MedidaMinima, MedidaMaxima),
            clsSolicitudCosteo.Rango($"posiciones[{numero}].alto_cm", AltoCm, MedidaMinima, MedidaMaxima),
            clsSolicitudCosteo.Rango($"posiciones[{numero}].peso_kg", PesoKg, 0m, clsSolicitudCosteo.MaximoPeso));
    }
}
