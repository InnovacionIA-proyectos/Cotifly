using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Precio.DTOs;

/// <summary>
/// Salida de <c>calcular_precio</c> (esquema <c>SalidaPrecio</c> de <c>specs/openapi.yaml</c>:
/// <c>{precio, costo, margen, metodo, utilidad, moneda?, trm_aplicada?, precio_cop?}</c>).
/// </summary>
/// <param name="Precio">Precio al cliente en <paramref name="Moneda"/>: pesos enteros en COP, 2 decimales en USD.</param>
/// <param name="Costo">Costo en COP (el costeo siempre es en pesos).</param>
/// <param name="Utilidad">Precio en pesos − costo, en COP.</param>
/// <param name="Moneda"><c>COP</c> o <c>USD</c> (HU-012.2).</param>
/// <param name="TrmAplicada">TRM con que se convirtió el precio; 0 en COP.</param>
/// <param name="PrecioCop">Precio en pesos antes de convertir; <c>null</c> en COP (es el mismo <paramref name="Precio"/>).</param>
public sealed record clsSalidaPrecio(
    decimal Precio,
    decimal Costo,
    decimal Margen,
    string Metodo,
    decimal Utilidad,
    string Moneda = clsConversorMoneda.Pesos,
    decimal TrmAplicada = 0m,
    decimal? PrecioCop = null)
{
    /// <summary>Precio en pesos (COP, sin TRM).</summary>
    public static clsSalidaPrecio Desde(clsPrecioCalculado calculado)
    {
        ArgumentNullException.ThrowIfNull(calculado);
        return new clsSalidaPrecio(calculado.Precio, calculado.Costo, calculado.Margen, calculado.Metodo, calculado.Utilidad);
    }

    /// <summary>Precio en pesos de esta salida (<see cref="PrecioCop"/>, o <see cref="Precio"/> si es en COP). Método y no propiedad: no se serializa.</summary>
    public decimal PrecioEnPesos() => PrecioCop ?? Precio;
}
