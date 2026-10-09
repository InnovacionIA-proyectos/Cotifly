namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Camión que se crea con una ruta capturada (HU-021.6). Mientras no exista el cubicador (#133) es uno solo, sin vehículo ni carrocería
/// (los resuelve la Mesa de Costeo de Nexo, decisión 7) y con el peso total de la ruta; el cubicador devolverá uno por fila de su
/// recomendación, con <paramref name="IdRecomendacionVehiculo"/>.
/// </summary>
public sealed record clsCamionPlaneado(string? TipoVehiculo, string? Carroceria, decimal? PesoKg, Guid? IdRecomendacionVehiculo = null);

/// <summary>
/// Una ruta lista para guardarse con <see cref="clsCotizacionSolicitud.RegistrarCaptura"/> (HU-021.6): la línea de la captura ya validada
/// y con sus municipios resueltos, la línea de negocio (id de <c>MargenesPolitica</c>), la fila del Excel de la que salió (0 en el
/// formulario), sus camiones y sus servicios conexos con el costo del catálogo de Nexo como precio (decisión 3).
/// </summary>
public sealed record clsRutaCapturada(
    clsLineaSolicitudCosteo Linea,
    int? LineaNegocioId,
    int? RutaOrigenIndice,
    IReadOnlyList<clsCamionPlaneado> Camiones,
    IReadOnlyList<clsCambioConexo> Conexos);
