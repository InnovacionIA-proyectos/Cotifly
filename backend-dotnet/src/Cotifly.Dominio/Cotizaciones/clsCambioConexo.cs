namespace Cotifly.Dominio.Cotizaciones;

/// <summary>Un conexo para <see cref="clsCotizacionSolicitud.CambiarConexos"/>: id de Nexo, nombre, costo unitario de Nexo y cantidad.</summary>
public sealed record clsCambioConexo(string IdServicioConexo, string? Nombre, decimal Precio, decimal Cantidad);
