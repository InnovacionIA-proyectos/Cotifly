namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// La ruta a la que aplica una petición de la etapa final (margen o conexos; HU-021.8, #135, decisión 6): su <c>linea_orden</c> o su
/// <c>linea_solicitud_id</c>, o ninguno. La resuelve el dominio (<c>clsCotizacionSolicitud.ResolverRuta</c>): sin ninguno, la única ruta;
/// con varias, <c>RUTA_REQUERIDA</c>.
/// </summary>
public sealed record clsRutaIndicada(int? LineaOrden = null, Guid? LineaSolicitudId = null)
{
    /// <summary>La petición no dice la ruta (vale solo si la cotización tiene una).</summary>
    public static clsRutaIndicada Ninguna { get; } = new();

    /// <summary>La ruta de orden <paramref name="lineaOrden"/>.</summary>
    public static clsRutaIndicada DeOrden(int lineaOrden) => new(lineaOrden);
}
