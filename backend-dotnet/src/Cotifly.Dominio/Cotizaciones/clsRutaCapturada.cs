namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Camión que se crea con una ruta capturada (HU-021.6). Con el cubicador (HU-021.5, #133) es uno por fila de su recomendación, con el
/// vehículo, la carrocería, el peso total del vehículo (C2) y <paramref name="IdRecomendacionVehiculo"/> (C3); sin vehículo ni carrocería
/// si el cubicador no seleccionó uno (lo resuelve la Mesa de Costeo de Nexo, decisión 7). Sin cubicador (respaldo) es uno solo, sin
/// vehículo, con el peso total de la ruta. HU-023.1 (#156): con el contrato del cubicador, el camión trae además el volumen que lleva y
/// los dos porcentajes de llenado (los guarda HU-023.3 con las columnas del script <c>02</c>).
/// </summary>
public sealed record clsCamionPlaneado(
    string? TipoVehiculo,
    string? Carroceria,
    decimal? PesoKg,
    Guid? IdRecomendacionVehiculo = null,
    decimal? VolumenTotalM3 = null,
    decimal? PorcentajeLlenadoVolumen = null,
    decimal? PorcentajeLlenadoPeso = null);

/// <summary>
/// Una ruta lista para guardarse con <see cref="clsCotizacionSolicitud.RegistrarCaptura"/> (HU-021.6): la línea de la captura ya validada
/// y con sus municipios resueltos, la línea de negocio (id de <c>MargenesPolitica</c>), la fila del Excel de la que salió (0 en el
/// formulario), sus camiones y sus servicios conexos con el costo del catálogo de Nexo como precio (decisión 3). <paramref name="RutaId"/>
/// es el id que tendrá la ruta (<c>CotizacionLineasSolicitud.id</c>): se fija antes de guardar porque viaja al cubicador como
/// <c>IdLineaSolicitud</c> (HU-021.5, #133); <c>null</c> = uno nuevo.
/// </summary>
public sealed record clsRutaCapturada(
    clsLineaSolicitudCosteo Linea,
    int? LineaNegocioId,
    int? RutaOrigenIndice,
    IReadOnlyList<clsCamionPlaneado> Camiones,
    IReadOnlyList<clsCambioConexo> Conexos,
    Guid? RutaId = null);
