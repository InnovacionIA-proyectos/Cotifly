namespace Cotifly.Dominio.Precio;

/// <summary>
/// Resultado de <see cref="clsCalculadoraPrecio"/>.
/// </summary>
/// <param name="Precio">Precio al cliente en pesos enteros.</param>
/// <param name="Costo">Costo recibido.</param>
/// <param name="Margen">Margen aplicado (fracción decimal).</param>
/// <param name="Metodo"><c>margen</c> o <c>markup</c>, en minúsculas.</param>
/// <param name="Utilidad">Precio − costo, redondeada a 2 decimales.</param>
public sealed record clsPrecioCalculado(decimal Precio, decimal Costo, decimal Margen, string Metodo, decimal Utilidad);
