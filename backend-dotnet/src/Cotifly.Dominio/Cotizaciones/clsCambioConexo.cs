namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Un conexo para <see cref="clsCotizacionSolicitud.CambiarConexos"/>: id de Nexo, nombre, costo unitario de Nexo y cantidad. HU-014 (#188):
/// <paramref name="IdServicioConexoCatalogo"/> es el id del CATÁLOGO de Nexo (script 03; igual a <paramref name="IdServicioConexo"/> en un
/// conexo capturado) y <paramref name="Categoria"/> su categoría en el catálogo (script 05); <c>null</c> si no se conocen.
/// </summary>
public sealed record clsCambioConexo(
    string IdServicioConexo, string? Nombre, decimal Precio, decimal Cantidad, string? IdServicioConexoCatalogo = null, string? Categoria = null);

/// <summary>
/// Un cambio del comercial sobre un conexo de la ruta (HU-021.8, #135): <paramref name="CantidadNueva"/> <c>null</c> = se QUITÓ (la fila
/// se borra); si no, la cantidad cambió. Sin montos: es lo que queda en la auditoría.
/// </summary>
public sealed record clsMovimientoConexo(string IdServicioConexo, string? Nombre, decimal CantidadAnterior, decimal? CantidadNueva)
{
    /// <summary>¿Se quitó de la ruta?</summary>
    public bool Quitado => CantidadNueva is null;
}
