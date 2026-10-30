using Cotifly.Aplicacion.Costeo.DTOs;

namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// <c>AgregarServiciosConexosWeb</c> (HU-013.3): de 1 a 20 conexos, cada uno para una línea de la cotización. Nunca lleva costos ni
/// valores. Los límites los valida <c>clsServiciosConexosCosteoService</c> (400 <c>ErrorWeb</c>), no MVC.
/// </summary>
public sealed record clsAgregarServiciosConexosWebRequest(IReadOnlyList<clsConexoAgregarWebRequest>? Items)
{
    /// <summary>Entrada del caso de uso, en el mismo orden (<c>null</c> si no vino <c>items</c>).</summary>
    public IReadOnlyList<clsConexoAgregarEntrada>? AEntrada() =>
        Items is null ? null : [.. Items.Select(i => i is null ? null! : new clsConexoAgregarEntrada(i.LineaOrden, i.ServicioId, i.Cantidad, i.LineaSolicitudId))];
}

/// <summary>
/// <c>ConexoAgregarWeb</c>: la ruta (<c>linea_orden</c> o <c>linea_solicitud_id</c>; opcional si la cotización tiene una sola, HU-021.8),
/// <c>servicio_id</c> del catálogo y <c>cantidad</c>. <c>linea_solicitud_id</c> se lee como texto para responder 400 <c>ErrorWeb</c>
/// con un mensaje claro si no es un UUID.
/// </summary>
public sealed record clsConexoAgregarWebRequest(int? LineaOrden, string? ServicioId, decimal? Cantidad, string? LineaSolicitudId = null);
