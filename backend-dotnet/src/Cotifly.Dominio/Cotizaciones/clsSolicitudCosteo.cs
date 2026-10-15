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
    public const int LargoNotas = 2000;
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
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    /// <summary>Encabezado (<c>notas</c> de Nexo, máx. 2000): nota para la Mesa de Nexo, distinta de la personalización del PDF.</summary>
    public string? Notas { get; init; }

    public required string Origen { get; init; }
    public required string Destino { get; init; }

    /// <summary>Línea (<c>origenMunicipioId</c>): id de <c>tbl_municipios</c> de Nexo elegido en <c>GET /web/municipios</c>.</summary>
    public int? OrigenMunicipioId { get; init; }

    /// <summary>Línea (<c>destinoMunicipioId</c>): igual que <see cref="OrigenMunicipioId"/>.</summary>
    public int? DestinoMunicipioId { get; init; }
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
            clsValidacion.LargoMaximo(CodigoError, "notas", Notas, LargoNotas),
            IdMunicipio("origen_municipio_id", OrigenMunicipioId),
            IdMunicipio("destino_municipio_id", DestinoMunicipioId),
            clsValidacion.LargoMaximo(CodigoError, "tipo_vehiculo", TipoVehiculo, LargoCatalogo),
            clsValidacion.LargoMaximo(CodigoError, "carroceria", Carroceria, LargoCatalogo),
            clsValidacion.LargoMaximo(CodigoError, "tipologia", Tipologia, LargoCorto),
            clsValidacion.LargoMaximo(CodigoError, "clase_viaje", ClaseViaje, LargoCorto),
            clsValidacion.LargoMaximo(CodigoError, "clase_contenedor", ClaseContenedor, LargoClaseContenedor),
            clsValidacion.LargoMaximo(CodigoError, "ciudad_devolucion_contenedor", CiudadDevolucionContenedor, LargoCiudad),
            clsValidacion.LargoMaximo(CodigoError, "ciudad_levante_contenedor", CiudadLevanteContenedor, LargoCiudad),
            Rango("horas_cargue", HorasCargue, 0m, MaximoHoras),
            Rango("horas_descargue", HorasDescargue, 0m, MaximoHoras),
            Rango("valor_aprovisionamiento", ValorAprovisionamiento, 0m, MaximoValor),
            Rango("peso_kg", PesoKg, 0m, MaximoPeso),
            Rango("valor_mercancia", ValorMercancia, 0m, MaximoValor),
            Posiciones.Count > MaximoPosiciones
                ? new clsErrorDominio(CodigoError, $"Una solicitud admite máximo {MaximoPosiciones} posiciones.")
                : null);

        return error
            ?? ValidarCondicionales()
            ?? Posiciones.Select((p, i) => p is null
                ? new clsErrorDominio(CodigoError, $"La posición {i + 1} está vacía.")
                : p.Validar(i + 1)).FirstOrDefault(e => e is not null)
            ?? ValidarPeso();
    }

    /// <summary>
    /// Validaciones condicionales de la captura (G3, HU-013.10): <c>clase_contenedor</c> solo con <c>clase_viaje = Contenedor</c>
    /// y obligatoria con ella, y <c>valor_aprovisionamiento</c> solo con una <c>ciudad_levante_contenedor</c> distinta del origen
    /// (Nexo lo descarta en otro caso). La regla es la de cada fila de una licitación (<see cref="clsLineaSolicitudCosteo.ErroresCondicionales"/>).
    /// </summary>
    private clsErrorDominio? ValidarCondicionales() =>
        ALinea().ErroresCondicionales() is [var primero, ..] ? new clsErrorDominio(CodigoError, primero.Mensaje) : null;

    /// <summary>Id de municipio de Nexo: entero positivo si viene.</summary>
    internal static clsErrorDominio? IdMunicipio(string campo, int? valor) =>
        valor is <= 0 ? new clsErrorDominio(CodigoError, $"El campo '{campo}' debe ser un id de municipio de Nexo (entero positivo).") : null;

    /// <summary>El encabezado de la solicitud (lo común a todas sus líneas, ADR-017).</summary>
    public clsEncabezadoSolicitudCosteo Encabezado => new()
    {
        Tipo = Tipo,
        Deadline = Deadline,
        HorasCargue = HorasCargue,
        HorasDescargue = HorasDescargue,
        Notas = Notas,
    };

    /// <summary>La única línea del formulario como línea de solicitud (ADR-017: una cotización de una línea es el caso N = 1).</summary>
    public clsLineaSolicitudCosteo ALinea() => new()
    {
        Origen = Origen,
        Destino = Destino,
        OrigenMunicipioId = OrigenMunicipioId,
        DestinoMunicipioId = DestinoMunicipioId,
        TipoVehiculo = TipoVehiculo,
        Carroceria = Carroceria,
        Tipologia = Tipologia,
        ClaseViaje = ClaseViaje,
        ClaseContenedor = ClaseContenedor,
        Exclusivo = Exclusivo,
        Consolidado = Consolidado,
        RetornaContenedor = RetornaContenedor,
        CiudadDevolucionContenedor = CiudadDevolucionContenedor,
        LevanteContenedorDiferente = LevanteContenedorDiferente,
        CiudadLevanteContenedor = CiudadLevanteContenedor,
        ValorAprovisionamiento = ValorAprovisionamiento,
        PesoKg = PesoKg,
        ValorMercancia = ValorMercancia,
        Posiciones = Posiciones,
    };

    /// <summary>Formulario de una línea a partir del encabezado y una línea (inverso de <see cref="Encabezado"/> y <see cref="ALinea"/>).</summary>
    public static clsSolicitudCosteo Desde(clsEncabezadoSolicitudCosteo encabezado, clsLineaSolicitudCosteo linea)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(linea);
        return new clsSolicitudCosteo
        {
            Tipo = encabezado.Tipo,
            Deadline = encabezado.Deadline,

            // G5 (HU-013.11): las horas de la línea, si las trae, son las que viajan a Nexo; si no, las del encabezado.
            HorasCargue = linea.HorasCargue ?? encabezado.HorasCargue,
            HorasDescargue = linea.HorasDescargue ?? encabezado.HorasDescargue,
            Notas = encabezado.Notas,
            Origen = linea.Origen,
            Destino = linea.Destino,
            OrigenMunicipioId = linea.OrigenMunicipioId,
            DestinoMunicipioId = linea.DestinoMunicipioId,
            TipoVehiculo = linea.TipoVehiculo,
            Carroceria = linea.Carroceria,
            Tipologia = linea.Tipologia,
            ClaseViaje = linea.ClaseViaje,
            ClaseContenedor = linea.ClaseContenedor,
            Exclusivo = linea.Exclusivo,
            Consolidado = linea.Consolidado,
            RetornaContenedor = linea.RetornaContenedor,
            CiudadDevolucionContenedor = linea.CiudadDevolucionContenedor,
            LevanteContenedorDiferente = linea.LevanteContenedorDiferente,
            CiudadLevanteContenedor = linea.CiudadLevanteContenedor,
            ValorAprovisionamiento = linea.ValorAprovisionamiento,
            PesoKg = linea.PesoKg,
            ValorMercancia = linea.ValorMercancia,
            Posiciones = linea.Posiciones,
        };
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

    /// <summary>Decimales que guardan las columnas del 0004 (<c>DECIMAL(18,2)</c>, <c>(12,2)</c>, <c>(10,2)</c>, <c>(6,2)</c>).</summary>
    public const int Decimales = 2;

    /// <summary>
    /// Rango de un número y máximo <see cref="Decimales"/> decimales: SQL Server redondearía en silencio un tercer decimal y lo
    /// guardado ya no sería lo que se envió a Nexo (ni lo que se reenviaría en un reintento).
    /// </summary>
    internal static clsErrorDominio? Rango(string campo, decimal? valor, decimal minimo, decimal maximo) =>
        valor is not { } v ? null
        : v < minimo || v > maximo ? new clsErrorDominio(CodigoError, $"El campo '{campo}' debe estar entre {minimo} y {maximo}.")
        : v != Math.Round(v, Decimales) ? new clsErrorDominio(CodigoError, $"El campo '{campo}' admite máximo {Decimales} decimales.")
        : null;
}

/// <summary>Pieza o bulto a transportar (<c>CapturaPosicionRequest</c> de Nexo), dimensiones en centímetros y peso en kilogramos.</summary>
/// <remarks>
/// <paramref name="Descripcion"/> se guarda como <c>nombreMercancia</c>. <paramref name="CodigoMercancia"/> es el código estable del
/// catálogo del cubicador (HU-023: lo que viaja en <c>POST /recomendaciones</c> y lo que se guarda, HU-023.3). <paramref name="IdTipoMercancia"/>
/// es el uuid del catálogo que mandaba el contrato anterior (<c>id_tipo_mercancia</c>, obsoleto): la captura lo traduce al código con
/// el catálogo y ya no se guarda (el script <c>02</c> quitó la columna).
/// </remarks>
public sealed record clsPosicionCarga(
    string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg, Guid? IdTipoMercancia = null,
    string? CodigoMercancia = null)
{
    /// <summary>Largo máximo de <see cref="CodigoMercancia"/> (<c>CotizacionMercancias.codigoMercancia NVARCHAR(64)</c>, script 02).</summary>
    public const int LargoCodigoMercancia = 64;

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
            clsValidacion.LargoMaximo(codigo, $"posiciones[{numero}].codigo_mercancia", CodigoMercancia, LargoCodigoMercancia),
            Cantidad is < 1 or > CantidadMaxima
                ? new clsErrorDominio(codigo, $"La cantidad de la posición {numero} debe estar entre 1 y {CantidadMaxima}.")
                : null,
            clsSolicitudCosteo.Rango($"posiciones[{numero}].largo_cm", LargoCm, MedidaMinima, MedidaMaxima),
            clsSolicitudCosteo.Rango($"posiciones[{numero}].ancho_cm", AnchoCm, MedidaMinima, MedidaMaxima),
            clsSolicitudCosteo.Rango($"posiciones[{numero}].alto_cm", AltoCm, MedidaMinima, MedidaMaxima),
            clsSolicitudCosteo.Rango($"posiciones[{numero}].peso_kg", PesoKg, 0m, clsSolicitudCosteo.MaximoPeso),
            // HU-023 (#176): la mercancía que viaja al cubicador (con tipo de mercancía) exige peso por unidad mayor que 0.
            VaAlCubicador() && PesoKg is not > 0m
                ? new clsErrorDominio(codigo, $"La mercancía {numero} debe traer el peso por unidad (peso_kg) mayor que 0: el cubicador lo exige.")
                : null);
    }

    /// <summary>¿La posición viaja al cubicador? (trae su tipo de mercancía: el código o, en la transición, el uuid anterior).</summary>
    public bool VaAlCubicador() => !string.IsNullOrWhiteSpace(CodigoMercancia) || IdTipoMercancia is { } id && id != Guid.Empty;
}
