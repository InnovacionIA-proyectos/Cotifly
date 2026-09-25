using System.Net;
using System.Net.Sockets;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Cotifly.Infraestructura.Tests.Correo;

/// <summary>
/// Transporte <c>smtp_relay</c> con MailKit contra un SMTP falso en proceso (sin red). Port de <c>tests/test_smtp_relay.py</c>:
/// remitente comercial y <c>Reply-To</c>, To/Cc, asunto, multipart texto + HTML con el PDF adjunto, remitente de respaldo y
/// errores; más envelope (<c>MAIL FROM</c>/<c>RCPT TO</c>), relay que rechaza destinatarios y cancelación.
/// </summary>
public sealed class clsTransporteSmtpRelayTests : IAsyncDisposable
{
    private readonly clsServidorSmtpFalso _relay = new();

    public ValueTask DisposeAsync() => _relay.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsTransporteSmtpRelay Transporte(int? puerto = null, int timeoutSeg = 20) =>
        new(Options.Create(new clsOpcionesCorreoTransporte
        {
            Habilitado = true,
            SmtpRelay = new clsOpcionesSmtpRelay
            {
                Host = clsServidorSmtpFalso.Host,
                Port = puerto ?? _relay.Puerto,
                FromFallback = "notificaciones@boterosoto.com.co",
                TimeoutSeg = timeoutSeg,
            },
        }), new clsRelojFalso());

    private static clsMensajeCorreo Mensaje(string? remitente = "nvargas@boterosoto.com.co") =>
        new(remitente,
            ["cliente@empresa.com"],
            ["jefe@boterosoto.com.co"],
            "Cotización PCN-1 — Botero Soto",
            "Adjuntamos su cotización.",
            clsPlantillaCorreo.Html("Adjuntamos su cotización."),
            new clsAdjuntoCorreo("PCN-1.pdf", "%PDF-1.4 datos"u8.ToArray()));

    // test_relay_arma_mensaje_con_remitente_comercial_y_adjunto
    [Fact]
    public async Task ArmaElMensajeConElRemitenteComercialElCuerpoYElAdjunto()
    {
        var messageId = await Transporte().EnviarAsync(Mensaje(), Ct);

        messageId.Should().StartWith("<").And.EndWith("@boterosoto.com.co>");
        var recibido = _relay.Recibidos.Should().ContainSingle().Subject;
        recibido.Remitente.Should().Be("nvargas@boterosoto.com.co", "envelope MAIL FROM = comercial");
        recibido.Destinatarios.Should().Equal("cliente@empresa.com", "jefe@boterosoto.com.co");

        using var mensaje = MimeMessage.Load(new MemoryStream(recibido.Datos), Ct);
        mensaje.From.Mailboxes.Single().Address.Should().Be("nvargas@boterosoto.com.co");
        mensaje.ReplyTo.Mailboxes.Single().Address.Should().Be("nvargas@boterosoto.com.co");
        mensaje.To.Mailboxes.Single().Address.Should().Be("cliente@empresa.com");
        mensaje.Cc.Mailboxes.Single().Address.Should().Be("jefe@boterosoto.com.co");
        mensaje.Subject.Should().Be("Cotización PCN-1 — Botero Soto");
        ("<" + mensaje.MessageId + ">").Should().Be(messageId);
        mensaje.Body!.ContentType.MimeType.Should().Be("multipart/mixed");

        mensaje.TextBody.Should().Contain("Adjuntamos su cotización.");
        mensaje.HtmlBody.Should().Contain("Botero Soto</b> · Soluciones Logísticas");
        var adjunto = mensaje.Attachments.OfType<MimePart>().Should().ContainSingle().Subject;
        adjunto.FileName.Should().Be("PCN-1.pdf");
        adjunto.ContentType.MimeType.Should().Be("application/pdf");
        adjunto.ContentTransferEncoding.Should().Be(ContentEncoding.Base64);
        using var contenido = new MemoryStream();
        await adjunto.Content!.DecodeToAsync(contenido, Ct);
        contenido.ToArray().Should().Equal("%PDF-1.4 datos"u8.ToArray());
    }

    // test_relay_sin_confirmado_usa_fallback
    [Fact]
    public async Task SinCorreoDelComercialUsaElRemitenteDeRespaldo()
    {
        await Transporte().EnviarAsync(Mensaje(remitente: null), Ct);

        var recibido = _relay.Recibidos.Single();
        recibido.Remitente.Should().Be("notificaciones@boterosoto.com.co");
        using var mensaje = MimeMessage.Load(new MemoryStream(recibido.Datos), Ct);
        mensaje.From.Mailboxes.Single().Address.Should().Be("notificaciones@boterosoto.com.co");
    }

    [Fact]
    public async Task SiElRelayRechazaLosDestinatariosPideAutorizarLaIpDelServidor()
    {
        _relay.RechazarDestinatarios = true;

        var enviar = () => Transporte().EnviarAsync(Mensaje(), Ct);

        (await enviar.Should().ThrowAsync<clsEnvioCorreoException>())
            .Which.Message.Should().Contain("rechazó los destinatarios").And.Contain("habilitar la IP del servidor").And.Contain("Unable to relay");
        _relay.Recibidos.Should().BeEmpty();
    }

    // test_relay_fallo_de_red_eleva_error
    [Fact]
    public async Task UnRelayInalcanzableEsEnvioFallido()
    {
        var puertoCerrado = PuertoLibre();

        var enviar = () => Transporte(puertoCerrado, timeoutSeg: 5).EnviarAsync(Mensaje(), Ct);

        (await enviar.Should().ThrowAsync<clsEnvioCorreoException>()).Which.Message.Should().StartWith("Relay SMTP 127.0.0.1 falló");
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorNoSeDisfrazaDeFalloDelRelay()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var enviar = () => Transporte().EnviarAsync(Mensaje(), cancelacion.Token);

        await enviar.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task UnDestinatarioMalFormadoEsEnvioFallidoSinTocarElRelay()
    {
        var mensaje = Mensaje() with { Para = ["no es un correo"] };

        var enviar = () => Transporte().EnviarAsync(mensaje, Ct);

        await enviar.Should().ThrowAsync<clsEnvioCorreoException>();
        _relay.Recibidos.Should().BeEmpty();
    }

    private static int PuertoLibre()
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((IPEndPoint)escucha.LocalEndpoint).Port;
        escucha.Stop();
        return puerto;
    }
}
