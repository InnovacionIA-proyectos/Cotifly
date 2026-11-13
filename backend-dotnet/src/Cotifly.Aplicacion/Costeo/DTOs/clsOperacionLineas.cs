namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// A qué rutas aplica una operación masiva de la etapa final: la lista de <c>linea_orden</c> (<see cref="Lineas"/>) o
/// todas las rutas cotizables (<see cref="Todas"/> = <c>true</c>, las rechazadas por la Mesa no cuentan). Debe venir exactamente una de las
/// dos formas; la valida <c>clsCosteoLineasService</c>.
/// </summary>
public sealed record clsSeleccionLineas(IReadOnlyList<int?>? Lineas, bool? Todas);

/// <summary>
/// Margen manual para varias rutas en una operación (<c>PUT .../costeo/margen/lineas</c>): el mismo
/// <see cref="Margen"/> para todas o, con <see cref="RevertirSugerido"/>, cada una vuelve a su margen sugerido.
/// </summary>
public sealed record clsMargenLineasEntrada(clsSeleccionLineas Seleccion, decimal? Margen, bool? RevertirSugerido, string? Justificacion);

/// <summary>
/// Un servicio conexo del catálogo de Nexo para agregar (con <see cref="Cantidad"/>) o quitar (sin cantidad) en varias
/// rutas en una operación (<c>POST .../costeo/servicios-conexos/lineas</c> y <c>POST .../costeo/servicios-conexos/lineas/quitar</c>).
/// </summary>
public sealed record clsConexoLineasEntrada(clsSeleccionLineas Seleccion, string? ServicioId, decimal? Cantidad = null);
