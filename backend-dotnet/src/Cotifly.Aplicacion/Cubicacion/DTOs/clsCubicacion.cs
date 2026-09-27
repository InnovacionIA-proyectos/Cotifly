namespace Cotifly.Aplicacion.Cubicacion.DTOs;

/// <summary>
/// Ítem de carga a cubicar (bulto o grupo de bultos idénticos): dimensiones en <b>centímetros</b> y peso en
/// <b>kilogramos</b> — distinto del resto del catálogo "Plan de Cargue" de Nexo, que usa milímetros. Espejo reducido
/// de <c>clsItemCubicacionRequest</c> de Nexo (<c>POST /api/cubicacion</c>): se omite <c>tipoMercancia</c>, fuera de
/// alcance del formulario de cotización.
/// </summary>
/// <param name="Descripcion">Descripción libre del bulto (opcional).</param>
/// <param name="Cantidad">Cuántos bultos idénticos con estas medidas.</param>
/// <param name="LargoCm">Largo en centímetros.</param>
/// <param name="AnchoCm">Ancho en centímetros.</param>
/// <param name="AltoCm">Alto en centímetros.</param>
/// <param name="PesoKg">Peso en kilogramos de cada bulto (opcional).</param>
public sealed record clsItemCubicacion(string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);

/// <summary>
/// Resultado reducido de <c>POST /web/cubicacion</c>: el mejor vehículo único, la flota si hace falta más de uno,
/// advertencias y la detección de extradimensión. A propósito NO incluye las matrices de factibilidad por vehículo
/// (<c>Matrices</c> de Nexo, con sus criterios y colocaciones 3D) ni las disposiciones detalladas de la flota
/// (<c>SugerenciaFlotaDto.Disposiciones</c> de Nexo): demasiado detalle para un comercial armando una cotización
/// (decisión de alcance confirmada, no una omisión pendiente).
/// </summary>
/// <param name="Sugerencia">Mejor vehículo único, o <c>null</c> si ninguno de la flota estándar sirve.</param>
/// <param name="SugerenciaFlota">Flota sugerida cuando hace falta más de un vehículo, o <c>null</c>.</param>
/// <param name="Advertencias">Advertencias de la evaluación (ej. "sin unidades candidatas").</param>
/// <param name="Extradimension">Detección de piezas que exceden las medidas estándar, o <c>null</c>.</param>
public sealed record clsResultadoCubicacion(
    clsSugerenciaVehiculo? Sugerencia,
    clsSugerenciaFlota? SugerenciaFlota,
    IReadOnlyList<string> Advertencias,
    clsDeteccionExtradimension? Extradimension);

/// <summary>Mejor vehículo único para la carga evaluada. Espejo 1:1 de <c>clsSugerenciaVehiculoDto</c> de Nexo.</summary>
public sealed record clsSugerenciaVehiculo(
    string UnidadId,
    string UnidadNombre,
    string Banda,
    decimal FactorUtilizacion,
    string? ConfigVehiculo,
    string? Carroceria,
    string? ClaseViaje,
    string? ClaseContenedor);

/// <summary>
/// Flota sugerida cuando un solo vehículo no alcanza. Espejo de <c>clsSugerenciaFlotaDto</c> de Nexo salvo por
/// <c>Disposiciones</c> (colocaciones 3D detalladas por vehículo de la flota): se omite a propósito, fuera de alcance.
/// </summary>
public sealed record clsSugerenciaFlota(
    string UnidadId,
    string UnidadNombre,
    int CantidadVehiculos,
    decimal UtilizacionPromedio,
    string? ConfigVehiculo,
    string? Carroceria,
    string? ClaseViaje,
    string? ClaseContenedor);

/// <summary>Medidas estándar de referencia contra las que se detecta extradimensión. Espejo de <c>clsMedidasEstandarDto</c>.</summary>
public sealed record clsMedidasEstandar(decimal LargoMaxCm, decimal AnchoMaxCm, decimal AltoMaxCm, decimal PesoMaxKg);

/// <summary>Pieza que excede las medidas estándar. Espejo de <c>clsPiezaExcedidaDto</c> de Nexo.</summary>
public sealed record clsPiezaExcedida(string Descripcion, IReadOnlyList<string> Excesos, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);

/// <summary>Detección de extradimensión de la carga evaluada. Espejo de <c>clsDeteccionExtradimensionDto</c> de Nexo.</summary>
public sealed record clsDeteccionExtradimension(bool HayExtradimension, clsMedidasEstandar MedidasEstandar, IReadOnlyList<clsPiezaExcedida> Piezas, string? Sugerencia);
