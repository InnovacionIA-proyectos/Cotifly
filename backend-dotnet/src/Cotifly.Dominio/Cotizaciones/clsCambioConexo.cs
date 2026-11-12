namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Un conexo para <see cref="clsCotizacionSolicitud.CambiarConexos"/>: id de Nexo, nombre, costo unitario de Nexo y cantidad. HU-014 (#188):
/// <paramref name="IdServicioConexoCatalogo"/> es el id del CATÁLOGO de Nexo (script 03; igual a <paramref name="IdServicioConexo"/> en un
/// conexo capturado) y <paramref name="Categoria"/> su categoría en el catálogo (script 05); <c>null</c> si no se conocen. HU-043.2 (#251):
/// <paramref name="PrecioVenta"/> es el precio de venta UNITARIO (<c>precioVentaUnitario</c> de la cotización de Nexo o <c>precioVenta</c>
/// del catálogo) que se cotiza sin margen; <c>null</c> = no se conoce y el conexo se cotiza al costo (<paramref name="Precio"/>), la misma
/// regla de Nexo para un servicio sin precio de venta. HU-046.3 (#269): <paramref name="CodigoServicio"/> es el código del servicio en el
/// catálogo de Nexo (<c>codigoServicio</c>, script 21; p. ej. <c>AVT_AUTO</c>), con el que la política de seguros cruza los conexos;
/// <c>null</c> si no se conoce.
/// </summary>
public sealed record clsCambioConexo(
    string IdServicioConexo, string? Nombre, decimal Precio, decimal Cantidad, string? IdServicioConexoCatalogo = null, string? Categoria = null,
    decimal? PrecioVenta = null, string? CodigoServicio = null);

/// <summary>
/// Un cambio del comercial sobre un conexo de la ruta (HU-021.8, #135): <paramref name="CantidadNueva"/> <c>null</c> = se QUITÓ (la fila
/// se borra); si no, la cantidad cambió. Sin montos: es lo que queda en la auditoría.
/// </summary>
public sealed record clsMovimientoConexo(string IdServicioConexo, string? Nombre, decimal CantidadAnterior, decimal? CantidadNueva)
{
    /// <summary>¿Se quitó de la ruta?</summary>
    public bool Quitado => CantidadNueva is null;
}

/// <summary>HU-043.2 (#251): la venta unitaria que Nexo fijó para un conexo ya guardado en la ruta <paramref name="RutaId"/>.</summary>
public sealed record clsVentaConexo(Guid RutaId, string IdServicioConexo, decimal PrecioVenta);
