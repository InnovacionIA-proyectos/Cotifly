using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Precio.DTOs;

/// <summary>
/// Salida de <c>calcular_precio</c> (esquema <c>SalidaPrecio</c> de <c>specs/openapi.yaml</c>:
/// <c>{precio, costo, margen, metodo, utilidad}</c>).
/// </summary>
public sealed record clsSalidaPrecio(decimal Precio, decimal Costo, decimal Margen, string Metodo, decimal Utilidad)
{
    public static clsSalidaPrecio Desde(clsPrecioCalculado calculado)
    {
        ArgumentNullException.ThrowIfNull(calculado);
        return new clsSalidaPrecio(calculado.Precio, calculado.Costo, calculado.Margen, calculado.Metodo, calculado.Utilidad);
    }
}
