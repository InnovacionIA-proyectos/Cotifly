using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// Puerto de los servicios conexos de Nexo (ADR-018 §4). La implementación real usa el mismo cliente HTTP del costeo
/// (<c>NexoCosteo</c>: 30 s, reintentos solo en GET, nunca en POST) y la misma identidad del comercial
/// (<c>X-Portal-clsUsuario-Id</c>), con un límite de tamaño de respuesta. Nunca lanza por fallos de Nexo; los errores ya vienen
/// traducidos: <c>NEXO_NO_DISPONIBLE</c> (sin configurar, caído, sin respuesta a tiempo, 5xx, 401, comercial sin
/// <c>idUsuarioExterno</c> o respuesta demasiado grande), <c>NEXO_SIN_PERMISO</c> (403) y, solo al agregar, <c>CONEXO_INVALIDO</c>
/// (otro 4xx, con el código de Nexo en el detalle). La cancelación del llamador sí se propaga.
/// </summary>
public interface IClsServiciosConexosNexo
{
    /// <summary>Catálogo de servicios conexos (<c>GET /api/servicios-conexos?soloActivos=true&amp;q=&amp;categoria=&amp;page=&amp;pageSize=</c>), sin costos.</summary>
    Task<clsResultado<clsPaginaCatalogoConexosNexo>> BuscarAsync(Guid usuarioId, clsFiltroCatalogoConexos filtro, CancellationToken cancellationToken = default);

    /// <summary>Cotización de Nexo por su id (<c>GET /api/cotizaciones/{id}</c>): líneas (con su id) y conexos con su costo.</summary>
    Task<clsResultado<clsCotizacionNexo>> LeerCotizacionAsync(Guid usuarioId, string cotizacionNexoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Agrega los conexos a la cotización de Nexo (<c>POST /api/cotizaciones/{id}/conexos</c> con <c>{items: [{lineaId,
    /// servicioConexoId, cantidad}]}</c>, sin <c>valorAplicado</c>). No se reintenta ni se cancela a mitad: el POST de Nexo no es
    /// idempotente. Un ERROR significa que Nexo no lo aplicó (no se envió, 4xx). Si no hay error, el resultado dice si el efecto es
    /// seguro (cualquier 2xx, con la cotización que devolvió Nexo si su cuerpo se pudo leer) o incierto (tiempo agotado, red, 5xx).
    /// </summary>
    Task<clsResultado<clsAgregadoNexo>> AgregarAsync(
        Guid usuarioId, string cotizacionNexoId, IReadOnlyList<clsConexoParaNexo> items, CancellationToken cancellationToken = default);
}
