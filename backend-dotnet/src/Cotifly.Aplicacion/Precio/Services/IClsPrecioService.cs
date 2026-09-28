using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Precio.Services;

/// <summary>Caso de uso <c>calcular_precio</c>: precio al cliente a partir del costo y el margen, en COP o en USD (HU-012.2).</summary>
public interface IClsPrecioService
{
    /// <summary>Precio en pesos por <c>margen</c> o <c>markup</c>; <c>PRECIO_INVALIDO</c> si la entrada no permite calcularlo.</summary>
    clsResultado<clsSalidaPrecio> Calcular(decimal costo, decimal margen, string? metodo = "margen");

    /// <summary>
    /// Precio en la moneda de la cotización: en COP igual que <see cref="Calcular"/> (<c>trm_aplicada = 0</c>); en USD el precio en pesos
    /// convertido con la TRM oficial del día de negocio. Moneda no soportada → <c>PRECIO_INVALIDO</c>; TRM caída → <c>TRM_NO_DISPONIBLE</c>
    /// (no hay precio en USD).
    /// </summary>
    Task<clsResultado<clsSalidaPrecio>> CalcularAsync(decimal costo, decimal margen, string? metodo, string? moneda, CancellationToken cancellationToken = default);

    /// <summary>TRM del día de negocio para <paramref name="moneda"/>: 0 en COP; en USD la oficial, o <c>TRM_NO_DISPONIBLE</c>.</summary>
    Task<clsResultado<decimal>> TrmDelDiaAsync(string? moneda, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convierte una salida en pesos a <paramref name="moneda"/> con <paramref name="trm"/> (la de <see cref="TrmDelDiaAsync"/>): en COP la
    /// devuelve igual; en USD, <c>precio = round(precio_cop / trm, 2)</c> con <c>costo</c> y <c>utilidad</c> en COP.
    /// </summary>
    clsResultado<clsSalidaPrecio> EnMoneda(clsSalidaPrecio enPesos, string? moneda, decimal trm);
}
