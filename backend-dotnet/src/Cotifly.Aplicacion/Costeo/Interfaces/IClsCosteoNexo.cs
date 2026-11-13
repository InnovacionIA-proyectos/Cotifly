using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// Puerto del costeo en Nexo (ADR-015). La implementación real (<c>clsCosteoNexo</c>) identifica al comercial ante Nexo con
/// <c>X-Portal-clsUsuario-Id: {UsuariosWeb.idUsuarioExterno}</c>, igual que la búsqueda de clientes; sin <c>Nexo:UrlBase</c>,
/// Infraestructura registra uno que responde <c>COSTEO_NEXO_NO_DISPONIBLE</c>. Nunca lanza por fallos de Nexo: devuelve
/// <c>COSTEO_NEXO_NO_DISPONIBLE</c>, <c>COSTEO_NEXO_TIEMPO_AGOTADO</c> o <c>COSTEO_NEXO_RECHAZADA</c> (el código y el mensaje de
/// Nexo en el detalle). La cancelación del llamador sí se propaga.
/// </summary>
public interface IClsCosteoNexo
{
    /// <summary>¿Hay un Nexo configurado? (sin él, el polling no consulta.)</summary>
    bool EstaConfigurado { get; }

    /// <summary>
    /// Crea la solicitud en Nexo (<c>POST /api/captura-solicitudes</c>) con una sola línea. No se reintenta: el alta no es
    /// idempotente. <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> del comercial dueño de la cotización.
    /// </summary>
    Task<clsResultado<clsSolicitudCreadaNexo>> EnviarSolicitudAsync(Guid usuarioId, clsSolicitudNexo solicitud, CancellationToken cancellationToken = default);

    /// <summary>
    /// Estado y costeo de una solicitud: <c>GET /api/solicitudes/{id}</c> (estado y líneas; un 409 pasajero de concurrencia se
    /// reintenta una vez) y, salvo que esté <c>Descartada</c>, <c>GET /api/cotizaciones/por-solicitud/{id}</c> (la cotización de Nexo con
    /// sus fletes y servicios conexos, o ninguna si Nexo todavía no la crea). Solo LEE: el "crear o recuperar" (un <c>POST</c> que
    /// escribe en Nexo) queda solo para un Nexo sin esa ruta de lectura, como en <see cref="LeerCotizacionAsync"/>. Un id que Nexo ya
    /// no reconoce responde <c>COSTEO_NEXO_SOLICITUD_NO_ENCONTRADA</c>.
    /// </summary>
    Task<clsResultado<clsCosteoConsultadoNexo>> ConsultarCosteoAsync(Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La cotización de Nexo de una solicitud SIN escribir en Nexo (sin el <c>POST /api/cotizaciones</c> de "crear o recuperar" cuando
    /// ya se conoce el id). Lee como el polling: con <paramref name="cotizacionNexoId"/> (o el guardado para la
    /// solicitud) <c>GET /api/cotizaciones/{id}</c>; si esa lectura no la trae, <c>GET /api/cotizaciones/por-solicitud/{id}</c>. Solo con un
    /// Nexo sin esa ruta (404/405 sin código) usa el "crear o recuperar", una vez. Si Nexo todavía no tiene cotización para la solicitud
    /// (204), <c>COTIZACION_NO_ENTREGADA</c>; los fallos de Nexo, como en <see cref="ConsultarCosteoAsync"/>.
    /// </summary>
    Task<clsResultado<clsCotizacionNexo>> LeerCotizacionAsync(
        Guid usuarioId, string solicitudId, string? cotizacionNexoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca el resultado en Nexo (<c>POST /api/cotizaciones/{cotizacionNexoId}/resultado</c>) con
    /// <c>{resultado: "ganada"|"perdida", motivo?, comentario?}</c>. Un 409 de Nexo llega con su código:
    /// <c>RESULTADO_YA_DEFINIDO</c> o <c>COTIZACION_NO_ENTREGADA</c>. No se reintenta (POST).
    /// </summary>
    Task<clsResultado<clsCotizacionNexo>> MarcarResultadoAsync(
        Guid usuarioId, string cotizacionNexoId, clsResultadoNexo resultado, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rechaza (descarta) la solicitud en Nexo (<c>POST /api/solicitudes/{id}/rechazar</c> con
    /// <c>{motivo (1-100), comentario? (hasta 500)}</c>; los textos más largos se recortan). 204 → <see cref="ResultadoRechazoNexo.Descartada"/>;
    /// 409 <c>SOLICITUD_YA_DESCARTADA</c> → <see cref="ResultadoRechazoNexo.YaDescartada"/>; 404 <c>SOLICITUD_NO_ENCONTRADA</c> →
    /// <see cref="ResultadoRechazoNexo.NoEncontrada"/>. 403 → <c>NEXO_SIN_PERMISO</c>; cualquier otro fallo (5xx, red, tiempo agotado, otro
    /// 4xx) llega como error y la solicitud pudo quedar viva: quien llama no debe capturar otra. No se reintenta (POST).
    /// </summary>
    Task<clsResultado<ResultadoRechazoNexo>> RechazarSolicitudAsync(
        Guid usuarioId, string solicitudId, string motivo, string? comentario, CancellationToken cancellationToken = default);
}
