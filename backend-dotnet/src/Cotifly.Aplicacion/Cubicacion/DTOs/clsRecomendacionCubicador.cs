using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cubicacion.DTOs;

/// <summary>
/// Valores de <c>resultado</c> de <c>POST /recomendaciones</c> (contrato del cubicador, HU-023.1, #156). Los dos son respuestas 200: "sin
/// selección" no es un error (la ruta sigue con un camión sin vehículo que resuelve la Mesa de Costeo de Nexo).
/// </summary>
public static class clsResultadosRecomendacion
{
    /// <summary>El cubicador eligió los vehículos (uno o varios, de tipos distintos: flota mixta).</summary>
    public const string Asignado = "ASIGNADO";

    /// <summary>El cubicador no pudo elegir: viene con <c>motivoSinSeleccion</c> (<see cref="clsMotivosSinSeleccion"/>).</summary>
    public const string SinSeleccionAutomatica = "SIN_SELECCION_AUTOMATICA";

    /// <summary>Los valores que acepta el contrato (y el <c>CHECK</c> de <c>resultadoCubicacion</c> del script <c>02</c>).</summary>
    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal) { Asignado, SinSeleccionAutomatica };
}

/// <summary>
/// Valores de <c>motivoSinSeleccion</c> (contrato del cubicador). Los cuatro primeros son los del <c>CHECK</c> del script <c>02</c>;
/// <see cref="BusquedaAgotada"/> (HU-023, #176) lo agrega el script <c>07</c>: sin él, la ruta guarda el motivo en <c>NULL</c> y lo deja
/// como advertencia (Infraestructura lo detecta en <c>sys.check_constraints</c>).
/// </summary>
public static class clsMotivosSinSeleccion
{
    public const string Extradimensionado = "EXTRADIMENSIONADO";
    public const string SinCandidatosDimensionales = "SIN_CANDIDATOS_DIMENSIONALES";
    public const string SuperaKMax = "SUPERA_K_MAX";
    public const string SinCarroceriaCompatible = "SIN_CARROCERIA_COMPATIBLE";

    /// <summary>
    /// El buscador del cubicador se quedó sin intentos: es del algoritmo, no del negocio. Al comercial NO se le dice que la carga no cabe.
    /// </summary>
    public const string BusquedaAgotada = "BUSQUEDA_AGOTADA";

    /// <summary>Los cinco motivos del contrato vigente.</summary>
    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal)
    {
        Extradimensionado, SinCandidatosDimensionales, SuperaKMax, SinCarroceriaCompatible, BusquedaAgotada,
    };

    /// <summary>Los cuatro motivos que admite el <c>CHECK</c> del script <c>02</c> (sin el <c>07</c>).</summary>
    public static readonly IReadOnlySet<string> DelScript02 = new HashSet<string>(StringComparer.Ordinal)
    {
        Extradimensionado, SinCandidatosDimensionales, SuperaKMax, SinCarroceriaCompatible,
    };

    /// <summary>
    /// Mensaje para el comercial de cada motivo. El de <see cref="BusquedaAgotada"/> no dice que la carga no cabe: el buscador se quedó
    /// sin intentos y la Mesa de Costeo escoge el vehículo.
    /// </summary>
    public static string Mensaje(string? motivo) => motivo switch
    {
        Extradimensionado => "Hay piezas que exceden las medidas estándar: la Mesa de Costeo escoge el vehículo.",
        SinCandidatosDimensionales => "Ningún vehículo de la flota cumple las medidas de la carga: la Mesa de Costeo escoge el vehículo.",
        SuperaKMax => "La carga necesita más vehículos de los que combina el cubicador: la Mesa de Costeo escoge los vehículos.",
        SinCarroceriaCompatible => "Ninguna carrocería es compatible con las mercancías: la Mesa de Costeo escoge el vehículo.",
        BusquedaAgotada =>
            "El cubicador no alcanzó a terminar la búsqueda de vehículos (es un límite de su algoritmo, no de tu carga): la Mesa de Costeo escoge el vehículo.",
        _ => "El cubicador no seleccionó vehículo: la Mesa de Costeo escoge el vehículo.",
    };
}

/// <summary>
/// Clasificación de los errores del cubicador (HU-021.5, #133): una <b>caída</b> (tiempo agotado, 5xx, red, circuito abierto, llave
/// rechazada) se marca en <see cref="Dominio.Comun.clsErrorDominio.Datos"/> con <c>caida = true</c>. Un rechazo de la petición (400 del contrato) o una
/// respuesta inválida (<c>CUBICADOR_RESPUESTA_INVALIDA</c>) no son caídas: nunca van al respaldo (HU-023.1).
/// </summary>
public static class clsErroresCubicador
{
    /// <summary>Clave de <see cref="Dominio.Comun.clsErrorDominio.Datos"/> que marca una caída.</summary>
    public const string ClaveCaida = "caida";

    /// <summary>¿El error es una caída del cubicador (no un rechazo ni una respuesta inválida)?</summary>
    public static bool EsCaida(Dominio.Comun.clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Datos is { } datos && datos.TryGetValue(ClaveCaida, out var valor) && valor is true;
    }
}

/// <summary>
/// Petición de <c>POST /recomendaciones</c> para UNA ruta (HU-023.1, #156): <paramref name="IdLineaSolicitud"/> es el id de la ruta de
/// Cotifly (o <c>PRUEBA-&lt;guid&gt;</c> en la vista previa de HU-023.2), <paramref name="ClaseViajeSeleccionada"/> es una preferencia
/// opcional, <paramref name="UsuarioId"/> es el <c>idUsuarioExterno</c> del comercial en texto y cada mercancía va con su
/// <c>codigoMercancia</c>. El <c>origenSistema</c> siempre es <see cref="OrigenSistema"/>.
/// </summary>
public sealed record clsPeticionRecomendacion(
    string IdLineaSolicitud,
    string? ClaseViajeSeleccionada,
    string? UsuarioId,
    IReadOnlyList<clsMercanciaCubicador> Mercancias)
{
    /// <summary>Valor de <c>origenSistema</c>.</summary>
    public const string OrigenSistema = "COTIFLY";
}

/// <summary>
/// Mercancía que se manda al cubicador: <paramref name="CodigoMercancia"/> (código estable del catálogo, por ejemplo <c>BEBIDAS</c>),
/// cantidad, medidas en centímetros y peso por unidad en kilogramos.
/// </summary>
public sealed record clsMercanciaCubicador(string CodigoMercancia, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg)
{
    /// <summary>Tope del cubicador: unidades en total por petición (suma de <c>cantidad</c>; si no, <c>CUBICACION_DEMASIADO_GRANDE</c>).</summary>
    public const int MaximoUnidades = 10_000;

    /// <summary>Mensaje de <c>CUBICACION_DEMASIADO_GRANDE</c> para el comercial.</summary>
    public const string MensajeDemasiadoGrande =
        "La ruta lleva más de 10.000 unidades en total y el cubicador recibe como máximo 10.000 por cálculo: parte la carga en varias rutas.";

    /// <summary>¿Las mercancías suman más de <see cref="MaximoUnidades"/> unidades?</summary>
    public static bool ExcedeTope(IEnumerable<clsMercanciaCubicador> mercancias)
    {
        ArgumentNullException.ThrowIfNull(mercancias);
        return mercancias.Sum(m => (long)Math.Max(m.Cantidad, 0)) > MaximoUnidades;
    }

    /// <summary>La mercancía de la captura tal como viaja al cubicador, o <c>null</c> si no tiene <c>codigoMercancia</c>.</summary>
    public static clsMercanciaCubicador? Desde(clsPosicionCarga posicion)
    {
        ArgumentNullException.ThrowIfNull(posicion);
        return string.IsNullOrWhiteSpace(posicion.CodigoMercancia)
            ? null
            : new clsMercanciaCubicador(
                posicion.CodigoMercancia.Trim(), posicion.Cantidad, posicion.LargoCm, posicion.AnchoCm, posicion.AltoCm, posicion.PesoKg);
    }
}

/// <summary>
/// Un vehículo recomendado (respuesta <c>ASIGNADO</c>). Los siete primeros campos son obligatorios en el contrato: si falta alguno, el
/// adaptador rechaza la respuesta con <c>CUBICADOR_RESPUESTA_INVALIDA</c> (contra el fallo silencioso). <paramref name="PesoTotalKg"/> es
/// el peso que lleva ese camión y el que viaja a Nexo en su línea; <paramref name="NombreVehiculo"/> es opcional (el nombre sale del
/// catálogo <c>GET /vehiculos-catalogo</c>, decisión 4 de HU-023). HU-023 (#176): <paramref name="NumeroVehiculo"/> (desde 1),
/// <paramref name="AlertaBajoLlenado"/>, <paramref name="Heterogenea"/> y lo que lleva el camión por posición
/// (<paramref name="Mercancias"/>; dos filas pueden compartir código: quien agrupe por código suma, ver <see cref="TotalesPorCodigo"/>).
/// </summary>
public sealed record clsVehiculoRecomendado(
    Guid IdRecomendacionVehiculo,
    string CodigoVehiculo,
    string CodigoCarroceria,
    decimal PesoTotalKg,
    decimal VolumenTotalM3,
    decimal PorcentajeLlenadoVolumen,
    decimal PorcentajeLlenadoPeso,
    string? NombreVehiculo = null,
    int? NumeroVehiculo = null,
    bool AlertaBajoLlenado = false,
    bool Heterogenea = false,
    IReadOnlyList<clsMercanciaVehiculo>? Mercancias = null)
{
    /// <summary>Lo que lleva el camión agrupado por código de mercancía (sin distinguir mayúsculas), <b>sumando</b> las filas repetidas.</summary>
    public IReadOnlyList<clsMercanciaVehiculo> TotalesPorCodigo() =>
        [.. (Mercancias ?? [])
            .GroupBy(m => m.CodigoMercancia, StringComparer.OrdinalIgnoreCase)
            .Select(g => new clsMercanciaVehiculo(
                null, g.First().IdTipoMercancia, g.First().CodigoMercancia, g.Sum(m => m.Cantidad), g.Sum(m => m.PesoKg), g.Sum(m => m.VolumenM3)))];
}

/// <summary>
/// Una fila de lo que lleva un camión (<c>vehiculos[].mercancias[]</c>): <paramref name="IndicePosicion"/> es la posición (base 0) en
/// las mercancías de la petición; llega en <c>null</c> al reconsultar una corrida guardada antes de S5-HU-17.8 (agrupada por código).
/// </summary>
public sealed record clsMercanciaVehiculo(
    int? IndicePosicion, string? IdTipoMercancia, string CodigoMercancia, int Cantidad, decimal PesoKg, decimal VolumenM3);

/// <summary>
/// Respuesta de <c>POST /recomendaciones</c> para una ruta (HU-023.1, #156), ya validada por el adaptador:
/// <list type="bullet">
/// <item><see cref="clsResultadosRecomendacion.Asignado"/>: al menos un vehículo, cada uno completo.</item>
/// <item><see cref="clsResultadosRecomendacion.SinSeleccionAutomatica"/>: <see cref="MotivoSinSeleccion"/> es uno de
/// <see cref="clsMotivosSinSeleccion.Todos"/>; los vehículos se ignoran.</item>
/// </list>
/// <see cref="Condicionada"/> marca la ruta (va a la Mesa de Costeo); <see cref="MercanciasCondicionadas"/>, <see cref="Advertencias"/> y
/// <see cref="Extradimension"/> se devuelven al comercial pero no se guardan.
/// </summary>
public sealed record clsRecomendacionCubicador(
    Guid? IdRecomendacion,
    string Resultado,
    string? MotivoSinSeleccion,
    bool Condicionada,
    IReadOnlyList<clsMercanciaCondicionada> MercanciasCondicionadas,
    IReadOnlyList<clsVehiculoRecomendado> Vehiculos,
    IReadOnlyList<clsAdvertenciaCubicador> Advertencias,
    clsExtradimensionCubicador? Extradimension)
{
    /// <summary>¿El cubicador eligió vehículos?</summary>
    public bool Asignado => string.Equals(Resultado, clsResultadosRecomendacion.Asignado, StringComparison.Ordinal);

    /// <summary>¿El cubicador no pudo elegir (el camión queda sin vehículo para la Mesa de Costeo)?</summary>
    public bool SinSeleccion => string.Equals(Resultado, clsResultadosRecomendacion.SinSeleccionAutomatica, StringComparison.Ordinal);

    /// <summary>Lo que se guarda en la ruta (HU-023.3) y los avisos que se devuelven al comercial.</summary>
    public clsCubicacionRuta ACubicacionRuta() => new(
        IdRecomendacion, Resultado, MotivoSinSeleccion, Condicionada,
        new clsAvisosCubicacion(MercanciasCondicionadas, Advertencias, Extradimension));
}

/// <summary>
/// Un tipo de mercancía del catálogo del cubicador (<c>GET /tipos-mercancia</c>). <paramref name="Codigo"/> es lo que viaja en
/// <c>POST /recomendaciones</c> y lo que guarda Cotifly. <paramref name="Id"/> (uuid) solo sirve para traducir el campo obsoleto
/// <c>id_tipo_mercancia</c> durante la transición (HU-023.3).
/// </summary>
public sealed record clsTipoMercanciaCubicador(
    string Codigo,
    string Nombre,
    string? Categoria,
    bool Cubicable,
    bool CondicionadaCubicacion,
    string? ComentarioCondicion,
    bool Activo,
    Guid? Id = null);

/// <summary>Una carrocería del catálogo del cubicador (<c>GET /tipos-carroceria</c>).</summary>
public sealed record clsTipoCarroceriaCubicador(string Codigo, string Nombre, bool Activo);

/// <summary>
/// Un tipo de vehículo del catálogo canónico del cubicador (<c>GET /vehiculos-catalogo</c>, HU-023 #176; antes <c>/tipos-vehiculo</c>):
/// el código que guarda el camión (<c>2L2</c>, <c>C2</c>, <c>3S3</c>…), el nombre que se muestra en pantalla y en el PDF, y las
/// carrocerías compatibles (vacía si el catálogo no las trae).
/// </summary>
public sealed record clsTipoVehiculoCubicador(string Codigo, string Nombre, IReadOnlyList<string> Carrocerias, bool Activo);
