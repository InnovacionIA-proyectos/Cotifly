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

    /// <summary>
    /// #278 (DU §8): tope de NEXO (y del cubicador) de unidades en total por línea: la suma de <c>cantidad</c> de las mercancías CON
    /// MEDIDAS (las que puede usar el cubicador y, si todas las de la ruta las traen, viajan como <c>posiciones[]</c>; en una ruta mixta no
    /// viajan, pero la regla es la misma para no depender de las demás mercancías). Si se pasa, <c>CUBICACION_DEMASIADO_GRANDE</c>. A
    /// diferencia de <see cref="MaximoPosiciones"/>, que cuenta todas las mercancías de la ruta.
    /// </summary>
    public const int MaximoUnidades = 10_000;

    /// <summary>Mensaje del tope de <see cref="MaximoUnidades"/> para el comercial.</summary>
    public const string MensajeDemasiadasUnidades =
        "Las mercancías con medidas de la ruta suman más de 10.000 unidades y Nexo y el cubicador reciben como máximo 10.000 por línea: parte la carga en varias rutas.";

    public const decimal MaximoHoras = 240m;
    public const decimal MaximoPeso = 999_999.99m;
    public const decimal MaximoValor = 9_999_999_999_999.99m;

    /// <summary>Tolerancia de Nexo entre el peso de la línea y la suma de las posiciones (1 %, <c>CapturarSolicitudValidador</c>).</summary>
    public const decimal ToleranciaPeso = 0.01m;

    /// <summary>Código de Nexo (va en el detalle): la línea no trae peso &gt; 0 ni posiciones con peso.</summary>
    public const string PesoRequerido = "PESO_REQUERIDO";

    /// <summary>Código de Nexo (va en el detalle): el peso difiere más del 1 % de la suma de las posiciones.</summary>
    public const string PesoInconsistente = "PESO_INCONSISTENTE";

    /// <summary>HU-030.1 (#184): nombre en el contrato del peso total con tara de una ruta de contenedor.</summary>
    public const string CampoPesoConTara = "peso_con_tara_kg";

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

    /// <summary>HU-030.1 (#184): peso total con tara (ver <see cref="clsLineaSolicitudCosteo.PesoConTaraKg"/>).</summary>
    public decimal? PesoConTaraKg { get; init; }

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
            Rango(CampoPesoConTara, PesoConTaraKg, 0m, MaximoPeso),
            HoraFaltante(HorasCargue, HorasDescargue) is { } faltaHora
                ? new clsErrorDominio(CodigoError, MensajeHorasEnPareja(faltaHora), clsCodigosLineaSolicitud.CampoRequerido)
                : null,
            Posiciones.Count > MaximoPosiciones
                ? new clsErrorDominio(CodigoError, $"Una solicitud admite máximo {MaximoPosiciones} posiciones.")
                : null,
            ExcedeUnidades(Posiciones)
                ? new clsErrorDominio(CodigoError, MensajeDemasiadasUnidades, clsCodigosLineaSolicitud.CubicacionDemasiadoGrande)
                : null);

        return error
            ?? ValidarCondicionales()
            ?? Posiciones.Select((p, i) => p is null
                ? new clsErrorDominio(CodigoError, $"La mercancía {i + 1} está vacía.")
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

    /// <summary>
    /// #278 (DU §8): ¿las mercancías con medidas suman más de <see cref="MaximoUnidades"/> unidades? Las que no traen medidas nunca van al
    /// cubicador ni viajan como posiciones a NEXO, así que no cuentan; las que sí, cuentan siempre (también en una ruta mixta, donde no
    /// viajan como posiciones).
    /// </summary>
    public static bool ExcedeUnidades(IEnumerable<clsPosicionCarga?> posiciones)
    {
        ArgumentNullException.ThrowIfNull(posiciones);
        return posiciones.Where(p => p is { ConMedidas: true }).Sum(p => (long)Math.Max(p!.Cantidad, 0)) > MaximoUnidades;
    }

    /// <summary>
    /// #278 (DU §5): el campo que falta si solo viene una de las horas (<c>horas_cargue</c> o <c>horas_descargue</c>); <c>null</c> si
    /// vienen las dos o ninguna. NEXO las exige en pareja.
    /// </summary>
    public static string? HoraFaltante(decimal? cargue, decimal? descargue) =>
        (cargue, descargue) switch
        {
            (not null, null) => "horas_descargue",
            (null, not null) => "horas_cargue",
            _ => null,
        };

    /// <summary>Mensaje de <see cref="HoraFaltante"/> para el comercial.</summary>
    public static string MensajeHorasEnPareja(string falta) =>
        $"El campo '{falta}' es obligatorio cuando viene la otra hora: las horas de cargue y descargue van las dos o ninguna (sin ellas Nexo aplica 4 + 4).";

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
        PesoConTaraKg = PesoConTaraKg,
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
            PesoConTaraKg = linea.PesoConTaraKg,
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

    /// <summary>Subcampo de la cantidad en <see cref="clsErrorPosicion.Campo"/>.</summary>
    public const string CampoCantidad = "cantidad";

    /// <summary>Primer error de la posición número <paramref name="numero"/> (1 = la primera), o <c>null</c>.</summary>
    public clsErrorDominio? Validar(int numero) =>
        PrimerError(numero) is { } error ? new clsErrorDominio(clsSolicitudCosteo.CodigoError, error.Mensaje) : null;

    /// <summary>
    /// Primer error de la mercancía número <paramref name="numero"/> (1 = la primera) con el subcampo que lo causa
    /// (<c>descripcion</c>, <c>codigo_mercancia</c>, <see cref="CampoCantidad"/>, <c>largo_cm</c>, <c>ancho_cm</c>, <c>alto_cm</c> o
    /// <c>peso_kg</c>), o <c>null</c>. D6 (E2E 2026-10-16): el mensaje nombra la mercancía en lenguaje natural con el número base 1
    /// ("Mercancía 2: …") y nunca una ruta con índice (<c>posiciones[2]</c>), que chocaba con el campo base 0 del error; el campo con índice
    /// lo arma quien reporta (<c>mercancias[i]</c>, base 0, como el resto de errores de la captura). La misma regla la usa
    /// "Calcular cubicaje" (D1) antes de llamar al cubicador.
    /// </summary>
    public clsErrorPosicion? PrimerError(int numero)
    {
        var codigo = clsSolicitudCosteo.CodigoError;
        var prefijo = $"Mercancía {numero.ToString(System.Globalization.CultureInfo.InvariantCulture)}: ";
        clsErrorPosicion? Con(string campo, clsErrorDominio? error) =>
            error is null ? null : new clsErrorPosicion(campo, prefijo + char.ToLowerInvariant(error.Mensaje[0]) + error.Mensaje[1..]);

        return Con("descripcion", clsValidacion.LargoMaximo(codigo, "descripcion", Descripcion, LargoDescripcion))
            ?? Con("codigo_mercancia", clsValidacion.LargoMaximo(codigo, "codigo_mercancia", CodigoMercancia, LargoCodigoMercancia))
            ?? (Cantidad is < 1 or > CantidadMaxima
                ? new clsErrorPosicion(CampoCantidad, $"{prefijo}la cantidad debe estar entre 1 y {CantidadMaxima}.")
                : null)
            ?? ErrorMedidas(prefijo, Con)
            ?? Con("peso_kg", clsSolicitudCosteo.Rango("peso_kg", PesoKg, 0m, clsSolicitudCosteo.MaximoPeso))
            // HU-023 (#176): la mercancía que viaja al cubicador (con tipo de mercancía) exige peso por unidad mayor que 0. También rige al
            // revalidar lo guardado (reintento, re-solicitud): una ruta anterior a la regla con tipo de mercancía y sin peso no se reenvía y
            // responde este mensaje; se corrige con una versión nueva (re-solicitar con la carga completa). Auditoría B2: se deja así a
            // propósito (relajarla solo al revalidar exige llevar una bandera por las tres rutas de validación).
            ?? (VaAlCubicador() && PesoKg is not > 0m
                ? new clsErrorPosicion("peso_kg", $"{prefijo}el peso por unidad (peso_kg) debe ser mayor que 0: el cubicador lo exige.")
                : null)
            // #278 (DU §5 y §8): se exige a toda mercancía con medidas, porque el cubicador las usa y, si todas las de la ruta las traen,
            // viajan a NEXO como posiciones, donde pesoKg es obligatorio (en una ruta mixta no viajan, pero la regla no cambia). Es el peso
            // de UNA unidad: NEXO y el cubicador lo multiplican por la cantidad.
            ?? (ConMedidas && PesoKg is not > 0m
                ? new clsErrorPosicion("peso_kg",
                    $"{prefijo}el peso por unidad (peso_kg) es obligatorio y mayor que 0 cuando la mercancía trae medidas: es el peso de una unidad, no el total.")
                : null);
    }

    /// <summary>¿La posición viaja al cubicador? (trae su tipo de mercancía: el código o, en la transición, el uuid anterior).</summary>
    public bool VaAlCubicador() => !string.IsNullOrWhiteSpace(CodigoMercancia) || IdTipoMercancia is { } id && id != Guid.Empty;

    /// <summary>HU-032 (#197): ¿no trae ninguna medida? (el contrato manda 0 cuando <c>largo_cm</c>, <c>ancho_cm</c> y <c>alto_cm</c> no vienen).</summary>
    public bool SinMedidas => LargoCm == 0m && AnchoCm == 0m && AltoCm == 0m;

    /// <summary>HU-032 (#197): ¿trae las tres medidas? Solo una mercancía con medidas viaja a Nexo como posición.</summary>
    public bool ConMedidas => LargoCm > 0m && AnchoCm > 0m && AltoCm > 0m;

    /// <summary>
    /// HU-032 (#197): ¿se puede cubicar? Trae su tipo de mercancía (código o, en la transición, el uuid), las tres medidas y el peso por
    /// unidad mayor que 0: es lo que el cubicador necesita.
    /// </summary>
    public bool EsCubicable => VaAlCubicador() && ConMedidas && PesoKg is > 0m;

    /// <summary>
    /// HU-032 (#197): mercancía "no cubicable" que se acepta: no trae NINGUNA medida ni tipo de mercancía (solo descripción, cantidad o
    /// peso). Se guarda con la ruta y no viaja como posición a Nexo; la ruta no se cubica y usa el vehículo elegido.
    /// </summary>
    public bool EsNoCubicable => SinMedidas && !VaAlCubicador();

    /// <summary>
    /// HU-032 (#197): las medidas de la mercancía. Sin ninguna medida y sin tipo de mercancía es una mercancía no cubicable (válida). Con
    /// tipo de mercancía y sin medidas es un error: el comercial escogió la mercancía para cubicarla y el cubicador las necesita (si no las
    /// conoce, quita el código). Medidas parciales (por ejemplo, largo sin ancho) son un dato mal digitado: error en la primera que falta.
    /// Las que vienen deben estar entre <see cref="MedidaMinima"/> y <see cref="MedidaMaxima"/>.
    /// </summary>
    private clsErrorPosicion? ErrorMedidas(string prefijo, Func<string, clsErrorDominio?, clsErrorPosicion?> con)
    {
        if (SinMedidas)
        {
            return VaAlCubicador()
                ? new clsErrorPosicion("largo_cm",
                    $"{prefijo}trae el tipo de mercancía pero no sus medidas: escribe largo, ancho y alto en cm (el cubicador las necesita) o quita el código de mercancía.")
                : null;
        }

        foreach (var (campo, valor) in new[] { ("largo_cm", LargoCm), ("ancho_cm", AnchoCm), ("alto_cm", AltoCm) })
        {
            if (valor == 0m)
            {
                return new clsErrorPosicion(campo,
                    $"{prefijo}faltan medidas ({campo}): escribe largo, ancho y alto en cm, o deja las tres vacías si no las conoces.");
            }
        }

        return con("largo_cm", clsSolicitudCosteo.Rango("largo_cm", LargoCm, MedidaMinima, MedidaMaxima))
            ?? con("ancho_cm", clsSolicitudCosteo.Rango("ancho_cm", AnchoCm, MedidaMinima, MedidaMaxima))
            ?? con("alto_cm", clsSolicitudCosteo.Rango("alto_cm", AltoCm, MedidaMinima, MedidaMaxima));
    }
}

/// <summary>
/// HU-032 (#197): cómo se cubica una ruta según sus mercancías (<see cref="clsLineaSolicitudCosteo.Cubicabilidad"/>).
/// </summary>
public enum CubicabilidadRuta
{
    /// <summary>La ruta no trae mercancías (solo el peso): no se cubica; usa el vehículo elegido.</summary>
    SinMercancias,

    /// <summary>Todas sus mercancías son cubicables (<see cref="clsPosicionCarga.EsCubicable"/>): se llama al cubicador.</summary>
    Cubicable,

    /// <summary>Ninguna es cubicable: no se cubica; usa el vehículo elegido.</summary>
    NoCubicable,

    /// <summary>Unas sí y otras no: no se cubica; usa el vehículo elegido con el aviso <c>MERCANCIAS_NO_CUBICABLES</c>.</summary>
    Mixta,
}

/// <summary>
/// Error de una mercancía (<see cref="clsPosicionCarga.PrimerError"/>): <paramref name="Campo"/> es el subcampo del contrato
/// (<c>cantidad</c>, <c>largo_cm</c>…) y <paramref name="Mensaje"/> el texto para el comercial, con el número de la mercancía en base 1.
/// </summary>
public sealed record clsErrorPosicion(string Campo, string Mensaje);
