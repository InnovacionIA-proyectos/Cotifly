using Cotifly.Aplicacion.Precio.DTOs;

namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// Resultado de un ajuste negociado: el margen con origen <c>negociado</c>, la diferencia registrada en
/// <c>MargenAjustes</c> y, si se pidió con costo, el precio calculado con el margen manual.
/// </summary>
/// <param name="Margen">Salida <c>SalidaMargen</c> con origen <c>negociado</c>.</param>
/// <param name="Registrado">True cuando el ajuste quedó guardado.</param>
/// <param name="Diferencia">Aplicado − política (4 decimales, escala de la columna).</param>
/// <param name="AjusteId">Id de la fila de <c>MargenAjustes</c>.</param>
/// <param name="Precio">Precio con el margen negociado, o <c>null</c> si no se envió costo.</param>
public sealed record clsSalidaAjusteMargen(
    clsSalidaMargen Margen,
    bool Registrado,
    decimal Diferencia,
    Guid AjusteId,
    clsSalidaPrecio? Precio);
