using Cotifly.Dominio.Correo;

namespace Cotifly.Aplicacion.Correo.Repositories;

/// <summary>
/// Puerto de los correos enviados y sus destinatarios (<c>Correos</c>, <c>CorreoDestinatarios</c>). Solo consultas y altas:
/// la regla de deduplicación (ventana de 5 minutos, mismo conjunto de <c>para</c>) vive en <c>clsCorreoService</c>
/// (deuda de ADR-008 resuelta en #11).
/// </summary>
public interface IClsCorreoRepository
{
    void Agregar(clsCorreo correo);

    /// <summary>
    /// Envíos exitosos (<c>enviado</c>) de la cotización con <c>createdAt</c> ≥ <paramref name="desde"/>, con sus destinatarios
    /// (base de la deduplicación).
    /// </summary>
    Task<IReadOnlyList<clsCorreo>> ListarEnviadosDesdeAsync(Guid cotizacionId, DateTimeOffset desde, CancellationToken cancellationToken = default);

    /// <summary>
    /// Último envío exitoso del comercial a un cliente (<c>Cotizaciones.nitCliente</c>), con sus destinatarios, o <c>null</c>
    /// si nunca le ha enviado (<c>GET /web/clientes/{nit}/destinatarios-previos</c>). Solo considera cotizaciones del comercial.
    /// </summary>
    Task<clsCorreo?> ObtenerUltimoEnviadoAClienteAsync(Guid usuarioId, string nitCliente, CancellationToken cancellationToken = default);

    /// <summary>Correos de una cotización con sus destinatarios, del más reciente al más antiguo.</summary>
    Task<IReadOnlyList<clsCorreo>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);
}
