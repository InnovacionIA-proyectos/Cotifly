namespace Cotifly.Aplicacion.Tarifas.DTOs;

/// <summary>
/// Salida de <c>buscar_tarifa</c> (núcleo de <c>ResultadoTarifa</c> del Python). Las candidatas del tarifario
/// de Drive no se portan: la forma definitiva del costeo la fija la integración con Nexo (HU-005).
/// </summary>
/// <param name="Encontrada">True si hay un costo para la ruta.</param>
/// <param name="Costo">Costo en <paramref name="Moneda"/>, o <c>null</c> si no se encontró.</param>
/// <param name="Moneda">Moneda del costo (COP por defecto).</param>
/// <param name="Fuente">Fuente del costo (ej. <c>nexo</c>).</param>
/// <param name="Referencia">Identificador de la fuente (ej. id del costeo en Nexo), si aplica.</param>
public sealed record clsSalidaTarifa(bool Encontrada, decimal? Costo, string Moneda, string Fuente, string? Referencia = null);
