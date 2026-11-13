namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Camión que se crea con una ruta capturada. Con el cubicador es uno por fila de su recomendación, con el
/// vehículo, la carrocería, el peso total del vehículo y <paramref name="IdRecomendacionVehiculo"/>. Si
/// el cubicador no seleccionó uno, si falla, si no está configurado o si la ruta no tiene mercancías cubicables, es uno solo con el vehículo
/// ELEGIDO por el comercial (o sin vehículo si no eligió: lo resuelve la Mesa de Costeo de Nexo) y el peso total de la ruta (ADR-022).
/// Con el cubicador, el camión trae además el volumen que lleva y
/// los dos porcentajes de llenado (columnas del script <c>02</c>).
/// <paramref name="Rechazo"/> es el rechazo de la Mesa de Costeo que el camión trae de la cotización de origen al copiarse a una versión
/// <c>-vN</c>: nace rechazado, con el mismo motivo y fecha, y NO viaja a Nexo (<see cref="clsVehiculoLineaSolicitud.FueraDelEnvio"/>).
/// </summary>
public sealed record clsCamionPlaneado(
    string? TipoVehiculo,
    string? Carroceria,
    decimal? PesoKg,
    Guid? IdRecomendacionVehiculo = null,
    decimal? VolumenTotalM3 = null,
    decimal? PorcentajeLlenadoVolumen = null,
    decimal? PorcentajeLlenadoPeso = null,
    clsRechazoHeredado? Rechazo = null);

/// <summary>
/// El rechazo de la Mesa de Costeo de un camión que se copia a otra cotización (la <c>-vN</c> de la re-solicitud o de la
/// edición): el motivo y la fecha en que Cotifly lo vio. Es trazabilidad: el camión copiado queda rechazado y fuera del envío.
/// </summary>
public sealed record clsRechazoHeredado(string? Motivo, DateTimeOffset? RechazadaAt);

/// <summary>
/// Una ruta lista para guardarse con <see cref="clsCotizacionSolicitud.RegistrarCaptura"/>: la línea de la captura ya validada
/// y con sus municipios resueltos, la línea de negocio (id de <c>MargenesPolitica</c>), la fila del Excel de la que salió (0 en el
/// formulario), sus camiones y sus servicios conexos con el costo del catálogo de Nexo como precio. <paramref name="RutaId"/>
/// es el id que tendrá la ruta (<c>CotizacionLineasSolicitud.id</c>): se fija antes de guardar porque viaja al cubicador como
/// <c>IdLineaSolicitud</c>; <c>null</c> = uno nuevo. Con <paramref name="CubicacionPendiente"/> la ruta se
/// guarda con su camión provisional y se cubica después, en segundo plano (licitación con cubicador).
/// </summary>
public sealed record clsRutaCapturada(
    clsLineaSolicitudCosteo Linea,
    int? LineaNegocioId,
    int? RutaOrigenIndice,
    IReadOnlyList<clsCamionPlaneado> Camiones,
    IReadOnlyList<clsCambioConexo> Conexos,
    Guid? RutaId = null,
    clsCubicacionRuta? Cubicacion = null,
    bool CubicacionPendiente = false)
{
    /// <summary>
    /// Los avisos del vehículo de la ruta (<see cref="clsAvisosVehiculo"/>: el cubicador ajustó el elegido, se usó el elegido,
    /// mercancías no cubicables o elegido inválido); se guardan con las advertencias de la ruta. <c>null</c> = ninguno.
    /// </summary>
    public IReadOnlyList<clsAdvertenciaCubicador>? AvisosVehiculo { get; init; }
}

/// <summary>
/// Lo que respondió el cubicador para una ruta (columnas del script <c>02</c> en <c>CotizacionLineasSolicitud</c>): la
/// corrida (<paramref name="IdRecomendacion"/>, para volver a consultarla), el resultado (<c>ASIGNADO</c> o
/// <c>SIN_SELECCION_AUTOMATICA</c>), el motivo de "sin selección" y si la ruta quedó condicionada (va a la Mesa de Costeo).
/// <paramref name="Avisos"/> (mercancías condicionadas, advertencias y extradimensión) se devuelve al comercial pero NO se guarda.
/// </summary>
public sealed record clsCubicacionRuta(
    Guid? IdRecomendacion, string Resultado, string? MotivoSinSeleccion, bool Condicionada, clsAvisosCubicacion? Avisos = null);

/// <summary>
/// Plan de camiones de una ruta: los camiones (al menos uno) y lo que respondió el cubicador (<c>null</c> si la ruta no lo
/// llamó: sin cubicador configurado, sin mercancías cubicables o respaldo por una caída). <paramref name="Avisos"/> son los
/// avisos del vehículo de la ruta (<see cref="clsAvisosVehiculo"/>); <c>null</c> = ninguno.
/// </summary>
public sealed record clsPlanRuta(
    IReadOnlyList<clsCamionPlaneado> Camiones, clsCubicacionRuta? Cubicacion = null, IReadOnlyList<clsAdvertenciaCubicador>? Avisos = null);

/// <summary>Avisos del cubicador para una ruta que se devuelven al comercial y no se guardan.</summary>
public sealed record clsAvisosCubicacion(
    IReadOnlyList<clsMercanciaCondicionada> MercanciasCondicionadas,
    IReadOnlyList<clsAdvertenciaCubicador> Advertencias,
    clsExtradimensionCubicador? Extradimension);

/// <summary>Una mercancía que dejó la ruta condicionada (pendiente de aprobación): se devuelve al comercial, no se guarda.</summary>
public sealed record clsMercanciaCondicionada(string CodigoMercancia, string? Comentario);

/// <summary>
/// Una advertencia de la ruta (por ejemplo, del cubicador: "refrigerado = FURGON + alerta"); se devuelve al comercial y se guarda con la
/// ruta (<c>avisosCubicacion</c>). Los avisos del vehículo (<see cref="clsAvisosVehiculo"/>) llevan además el vehículo elegido
/// (<see cref="VehiculoElegido"/>) y el que quedó en los camiones (<see cref="VehiculosFinales"/>); en las demás, los dos son <c>null</c>.
/// </summary>
public sealed record clsAdvertenciaCubicador(string? Codigo, string Mensaje)
{
    /// <summary>El vehículo que eligió el comercial para la ruta (solo en los avisos del vehículo).</summary>
    public clsVehiculoAviso? VehiculoElegido { get; init; }

    /// <summary>El vehículo de cada camión que quedó en la ruta (solo en los avisos del vehículo).</summary>
    public IReadOnlyList<clsVehiculoAviso>? VehiculosFinales { get; init; }
}

/// <summary>
/// Extradimensión de la respuesta del cubicador (guía del cubicador §5.3): si alguna pieza excede las medidas estándar
/// (<paramref name="HayExtradimension"/>), las medidas estándar con las que se comparó y cada pieza que las excede, con la posición de
/// la petición (<c>indicePosicion</c>, base 0) y los excesos en texto. Se devuelve al comercial y se guarda en <c>avisosCubicacion</c>.
/// </summary>
public sealed record clsExtradimensionCubicador(
    bool HayExtradimension, clsMedidasEstandarCubicador? MedidasEstandar, IReadOnlyList<clsPiezaExtradimension> Piezas);

/// <summary>Las medidas estándar con las que el cubicador decide la extradimensión (cm y kg).</summary>
public sealed record clsMedidasEstandarCubicador(decimal? LargoMaxCm, decimal? AnchoMaxCm, decimal? AltoMaxCm, decimal? PesoMaxKg);

/// <summary>
/// Una pieza que excede las medidas estándar: <paramref name="IndicePosicion"/> es la posición (base 0) en las mercancías de la
/// petición (puede faltar), con su tipo de mercancía y los excesos (por ejemplo <c>"largo 1400.0 cm &gt; 1220.0 cm"</c>).
/// </summary>
public sealed record clsPiezaExtradimension(int? IndicePosicion, string? IdTipoMercancia, string? CodigoMercancia, IReadOnlyList<string> Excesos);
