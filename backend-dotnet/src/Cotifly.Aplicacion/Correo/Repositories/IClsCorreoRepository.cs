using Cotifly.Dominio.Correo;

namespace Cotifly.Aplicacion.Correo.Repositories;

/// <summary>
/// Puerto de los correos de la cotización y sus destinatarios (<c>Correos</c>, <c>CorreoDestinatarios</c>). La regla de deduplicación
/// (ventana de 5 minutos, mismo conjunto de <c>para</c>) vive en <c>clsCorreoService</c>. Un envío
/// "cuenta" si salió o sigue en curso en MailSender (<see cref="clsCorreo.CuentaComoEnvio"/>), y el worker de
/// correos consulta la agenda <c>siguienteConsultaAt</c>.
/// </summary>
public interface IClsCorreoRepository
{
    void Agregar(clsCorreo correo);

    /// <summary>
    /// Envíos que cuentan (<c>enviado</c>, <c>encolado</c> o <c>enviando</c>) de la cotización con <c>createdAt</c> ≥ <paramref name="desde"/>,
    /// con sus destinatarios (base de la deduplicación).
    /// </summary>
    Task<IReadOnlyList<clsCorreo>> ListarEnviadosDesdeAsync(Guid cotizacionId, DateTimeOffset desde, CancellationToken cancellationToken = default);

    /// <summary>
    /// Último envío que cuenta (<c>enviado</c>, <c>encolado</c> o <c>enviando</c>) del comercial a un cliente (<c>Cotizaciones.nitCliente</c>),
    /// con sus destinatarios, o <c>null</c> si nunca le ha enviado (<c>GET /web/clientes/{nit}/destinatarios-previos</c>). Solo considera
    /// cotizaciones del comercial.
    /// </summary>
    Task<clsCorreo?> ObtenerUltimoEnviadoAClienteAsync(Guid usuarioId, string nitCliente, CancellationToken cancellationToken = default);

    /// <summary>Correos de una cotización con sus destinatarios, del más reciente al más antiguo.</summary>
    Task<IReadOnlyList<clsCorreo>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);

    /// <summary>Un correo con sus destinatarios, con seguimiento de cambios (para actualizar su estado), o <c>null</c>.</summary>
    Task<clsCorreo?> ObtenerAsync(Guid correoId, CancellationToken cancellationToken = default);

    /// <summary>Correos con la consulta del estado vencida (<c>siguienteConsultaAt ≤ ahora</c>), los más atrasados primero.</summary>
    Task<IReadOnlyList<clsConsultaCorreoPendiente>> ListarConsultasPendientesAsync(DateTimeOffset ahora, int lote, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserva la consulta de un correo (varias réplicas): mueve <c>siguienteConsultaAt</c> de <paramref name="esperada"/> a
    /// <paramref name="reclamoHasta"/> en una sola sentencia. <c>false</c> si otra réplica ya la tomó.
    /// </summary>
    Task<bool> ReclamarConsultaAsync(Guid correoId, DateTimeOffset esperada, DateTimeOffset reclamoHasta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserva el reintento de un correo <c>fallido</c> con id de MailSender en una sola sentencia (pasa a <c>encolado</c>, con
    /// <paramref name="encoladoAt"/> y la consulta en <paramref name="consultaAt"/>). <c>false</c> si ya no está fallido: un doble clic o
    /// dos réplicas no piden dos <c>/retry</c> (MailSender correría dos entregas a la vez y el cliente recibiría el correo dos veces).
    /// </summary>
    Task<bool> ReclamarReintentoAsync(Guid correoId, DateTimeOffset encoladoAt, DateTimeOffset consultaAt, CancellationToken cancellationToken = default);

    /// <summary>Deshace la reserva de <see cref="ReclamarReintentoAsync"/> cuando MailSender no aceptó el reintento (vuelve a <c>fallido</c>).</summary>
    Task LiberarReintentoAsync(Guid correoId, DateTimeOffset encoladoAt, CancellationToken cancellationToken = default);
}

/// <summary>Un correo de la agenda del worker (su id y la consulta reservada).</summary>
public sealed record clsConsultaCorreoPendiente(Guid Id, DateTimeOffset SiguienteConsultaAt);
