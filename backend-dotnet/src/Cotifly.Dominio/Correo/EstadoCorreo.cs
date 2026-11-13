namespace Cotifly.Dominio.Correo;

/// <summary>
/// Estado de un correo de la cotización (<c>Correos.estado</c>, CHECK <c>ck_correos_estado</c> del script 13, ADR-023):
/// MailSender encola el correo (202) y lo entrega en segundo plano, así que el estado es asíncrono:
/// <list type="bullet">
/// <item><see cref="Encolado"/>: MailSender lo aceptó (202) y no lo ha entregado al relay (<c>Queued</c>/<c>Draft</c>).</item>
/// <item><see cref="Enviando"/>: MailSender lo está entregando o programó un reintento por un error transitorio
/// (<c>Processing</c>/<c>RetryScheduled</c>).</item>
/// <item><see cref="Enviado"/>: el relay lo aceptó (<c>Sent</c>).</item>
/// <item><see cref="Fallido"/>: MailSender lo rechazó al encolarlo (400/413/422, caído o sin respuesta), se agotaron sus reintentos
/// (<c>Failed</c>/<c>Cancelled</c>) o no confirmó la entrega en la edad máxima de la consulta.</item>
/// </list>
/// </summary>
public enum EstadoCorreo
{
    Enviado,
    Fallido,
    Encolado,
    Enviando,
}
