using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace Cotifly.Infraestructura.Correo.Services;

/// <summary>
/// Transporte <c>smtp_relay</c> con MailKit (port de <c>EnviadorSmtpRelay</c> de <c>mcp_server/correo.py</c>): relay interno
/// de Botero Soto (Microsoft ESMTP sin AUTH ni STARTTLS, control por red/IP), conexión en texto plano al puerto configurado
/// (25) y sin login, igual que el Python.
/// <list type="bullet">
/// <item>Remitente (header <c>From</c> y envelope <c>MAIL FROM</c>) y <c>Reply-To</c>: el comercial; sin correo, el de respaldo.</item>
/// <item>Envelope <c>RCPT TO</c>: para + cc.</item>
/// <item><c>multipart/mixed</c> con <c>multipart/alternative</c> (texto + HTML) y el PDF en base64.</item>
/// <item><c>Message-ID</c> con dominio <c>boterosoto.com.co</c>, devuelto entre <c>&lt;&gt;</c> como <c>make_msgid</c>.</item>
/// <item>Relay que rechaza destinatarios (550 5.7.1 "Unable to relay") → mensaje que pide a TI autorizar la IP del servidor.</item>
/// </list>
/// </summary>
public sealed class clsTransporteSmtpRelay(IOptions<clsOpcionesCorreoTransporte> opciones, IClsReloj reloj) : IClsTransporteCorreo
{
    public const string DominioMessageId = "boterosoto.com.co";

    public string Modo => clsOpcionesCorreoTransporte.ModoSmtpRelay;

    public async Task<string> EnviarAsync(clsMensajeCorreo mensaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mensaje);
        var relay = opciones.Value.SmtpRelay;
        var host = relay.Host ?? string.Empty;
        try
        {
            var remitente = string.IsNullOrWhiteSpace(mensaje.Remitente) ? relay.FromFallback : mensaje.Remitente.Trim();
            var (mime, messageId) = Construir(mensaje, remitente, reloj.AhoraUtc);
            var destinatarios = mensaje.Para.Concat(mensaje.Cc).Select(MailboxAddress.Parse).ToList();

            using var cliente = new SmtpClient { Timeout = relay.TimeoutSeg * 1000 };
            await cliente.ConnectAsync(host, relay.Port, SecureSocketOptions.None, cancellationToken);
            await cliente.SendAsync(mime, MailboxAddress.Parse(remitente), destinatarios, cancellationToken);
            await cliente.DisconnectAsync(quit: true, cancellationToken);
            return messageId;
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted)
        {
            throw new clsEnvioCorreoException(
                $"El relay {host} rechazó los destinatarios: no está autorizado a relayar desde la IP de este servidor "
                + $"(o el dominio destino no está permitido). Pídele a TI habilitar la IP del servidor en el relay {host}. "
                + $"Detalle: {ex.Mailbox} → {(int)ex.StatusCode} {ex.Message}",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new clsEnvioCorreoException($"Relay SMTP {host} falló: {ex.Message}", ex);
        }
    }

    /// <summary>Mensaje MIME (<c>multipart/mixed</c>) y su <c>Message-ID</c> entre <c>&lt;&gt;</c>.</summary>
    internal static (MimeMessage Mensaje, string MessageId) Construir(clsMensajeCorreo mensaje, string remitente, DateTimeOffset fecha)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(remitente));
        mime.ReplyTo.Add(MailboxAddress.Parse(remitente));
        mime.To.AddRange(mensaje.Para.Select(MailboxAddress.Parse));
        mime.Cc.AddRange(mensaje.Cc.Select(MailboxAddress.Parse));
        mime.Subject = mensaje.Asunto;
        mime.Date = fecha;
        var id = MimeUtils.GenerateMessageId(DominioMessageId);
        mime.MessageId = id;

        var alternativa = new MultipartAlternative
        {
            new TextPart(MimeKit.Text.TextFormat.Plain) { Text = mensaje.CuerpoTexto },
            new TextPart(MimeKit.Text.TextFormat.Html) { Text = mensaje.CuerpoHtml },
        };
        var tipo = ContentType.Parse(mensaje.Adjunto.TipoMime);
        var adjunto = new MimePart(tipo.MediaType, tipo.MediaSubtype)
        {
            Content = new MimeContent(new MemoryStream(mensaje.Adjunto.Contenido)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = mensaje.Adjunto.Nombre,
        };
        mime.Body = new Multipart("mixed") { alternativa, adjunto };
        return (mime, $"<{id}>");
    }
}
