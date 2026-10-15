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
    /// Crea la solicitud en Nexo (<c>POST /api/captura-solicitudes</c>, HU-005.5) con una sola línea. No se reintenta: el alta no es
    /// idempotente. <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> del comercial dueño de la cotización.
    /// </summary>
    Task<clsResultado<clsSolicitudCreadaNexo>> EnviarSolicitudAsync(Guid usuarioId, clsSolicitudNexo solicitud, CancellationToken cancellationToken = default);

    /// <summary>
    /// Estado y costeo de una solicitud (HU-005.6): <c>GET /api/solicitudes/{id}</c> (estado y líneas) y, salvo que esté
    /// <c>Descartada</c>, <c>POST /api/cotizaciones {solicitudId}</c> ("crear o recuperar" la cotización de Nexo con sus fletes y
    /// servicios conexos). Un id que Nexo ya no reconoce responde <c>COSTEO_NEXO_RECHAZADA</c> con <c>HTTP 404</c> en el detalle.
    /// </summary>
    Task<clsResultado<clsCosteoConsultadoNexo>> ConsultarCosteoAsync(Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default);

    /// <summary>"Crear o recuperar" la cotización de Nexo de una solicitud (<c>POST /api/cotizaciones {solicitudId}</c>).</summary>
    Task<clsResultado<clsCotizacionNexo>> ObtenerCotizacionAsync(Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca el resultado en Nexo (<c>POST /api/cotizaciones/{cotizacionNexoId}/resultado</c>, HU-005.14) con
    /// <c>{resultado: "ganada"|"perdida", motivo?, comentario?}</c>. Un 409 de Nexo llega con su código:
    /// <c>RESULTADO_YA_DEFINIDO</c> o <c>COTIZACION_NO_ENTREGADA</c>. No se reintenta (POST).
    /// </summary>
    Task<clsResultado<clsCotizacionNexo>> MarcarResultadoAsync(
        Guid usuarioId, string cotizacionNexoId, clsResultadoNexo resultado, CancellationToken cancellationToken = default);
}
