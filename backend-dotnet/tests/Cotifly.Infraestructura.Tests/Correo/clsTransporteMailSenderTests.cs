using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Dominio.Correo;
using Cotifly.Infraestructura.Correo.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Correo;

/// <summary>
/// HU-033 (#199, ADR-023): transporte <c>mailsender</c> por el registro real (cliente HTTP tipado con su tubería de resiliencia) contra un
/// MailSender falso en proceso (<see cref="clsMailSenderFalso"/>): nunca sale a la red ni envía correos reales. Cubre el formulario
/// multipart (From, To/Cc/Bcc repetidos, Subject, Body HTML, IsHtml, Priority, Metadata y el PDF), el <c>X-Correlation-ID</c>, el 202, los
/// rechazos 400/413/422, la caída, el tiempo agotado, la consulta del estado (0-6) y el reintento.
/// </summary>
public sealed class clsTransporteMailSenderTests : IDisposable
{
    private readonly clsMailSenderFalso _mailSender = new();
    private readonly ServiceProvider _proveedor;
    private readonly IServiceScope _ambito;

    public clsTransporteMailSenderTests()
    {
        _proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso(), configuracionExtra: new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
            ["Correo:MailSender:TimeoutSegundos"] = "1",
            ["Correo:Proteccion:BuzonPruebas"] = clsMailSenderFalso.BuzonPruebas,
        }, configurar: s => s.AddHttpClient(clsTransporteMailSender.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => _mailSender));
        _ambito = _proveedor.CreateScope();
    }

    public void Dispose()
    {
        _ambito.Dispose();
        _proveedor.Dispose();
        _mailSender.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IClsTransporteCorreo Transporte => _ambito.ServiceProvider.GetRequiredService<IClsTransporteCorreo>();

    private IClsSeguimientoCorreoExterno Seguimiento => _ambito.ServiceProvider.GetRequiredService<IClsSeguimientoCorreoExterno>();

    private static clsMensajeCorreo Mensaje(string? remitente = "nvargas@boterosoto.com.co") =>
        new(remitente,
            ["cliente@empresa.com", "compras@empresa.com"],
            ["jefe@empresa.com"],
            "Cotización PCN-1 — Botero Soto",
            "Adjuntamos su cotización.",
            clsPlantillaCorreo.Html("Adjuntamos su cotización."),
            new clsAdjuntoCorreo("PCN-1.pdf", "%PDF-1.4 datos"u8.ToArray()))
        {
            Bcc = ["nvargas@boterosoto.com.co"],
            IdCorrelacion = "PCN-1:0d6c1f8a-1111-2222-3333-444455556666",
            Metadatos = new Dictionary<string, string?> { ["origen"] = "cotifly", ["folio"] = "PCN-1" },
        };

    [Fact]
    public async Task UnAltaAceptadaDevuelveElIdDeMailSenderYQuedaEncolada()
    {
        var aceptado = await Transporte.EnviarAsync(Mensaje(), Ct);

        var recibido = _mailSender.Recibidos.Should().ContainSingle().Subject;
        aceptado.IdExterno.Should().Be(recibido.Id);
        aceptado.Estado.Should().Be(EstadoCorreo.Encolado);
        recibido.From.Should().Be("nvargas@boterosoto.com.co");
        recibido.To.Should().Equal("cliente@empresa.com", "compras@empresa.com");
        recibido.Cc.Should().Equal("jefe@empresa.com");
        recibido.Bcc.Should().Equal("nvargas@boterosoto.com.co");
        recibido.Subject.Should().Be("Cotización PCN-1 — Botero Soto");
        recibido.IsHtml.Should().BeTrue();
        recibido.Body.Should().Contain("Adjuntamos su cotizaci&#243;n.").And.Contain(">Cotizaci&#243;n<");
        recibido.Priority.Should().Be("Normal");
        recibido.CorrelationId.Should().Be("PCN-1:0d6c1f8a-1111-2222-3333-444455556666");
        JsonDocument.Parse(recibido.Metadata!).RootElement.GetProperty("folio").GetString().Should().Be("PCN-1");
        var adjunto = recibido.Adjuntos.Should().ContainSingle().Subject;
        adjunto.Nombre.Should().Be("PCN-1.pdf");
        adjunto.Tipo.Should().Be("application/pdf");
        adjunto.Contenido.Should().Equal("%PDF-1.4 datos"u8.ToArray());
    }

    [Fact]
    public async Task SinCorreoDelComercialElRemitenteEsElPorDefecto()
    {
        await Transporte.EnviarAsync(Mensaje(remitente: null), Ct);

        _mailSender.Recibidos.Should().ContainSingle().Which.From.Should().Be("notificaciones@boterosoto.com.co");
    }

    [Theory]
    // Auditoría #199 (SPF/DMARC): el comercial va en el From solo si es UNA dirección válida de un dominio permitido.
    [InlineData("comercial", "boterosoto.com.co", "nvargas@boterosoto.com.co", "nvargas@boterosoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co", "NVargas@BoteroSoto.com.co", "NVargas@BoteroSoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co", "nvargas@gmail.com", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co", "Nathaly <nvargas@boterosoto.com.co>", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co", "sin-arroba", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co, bssl.com.co", "ana@bssl.com.co", "ana@bssl.com.co")]
    [InlineData("corporativo", "boterosoto.com.co", "nvargas@boterosoto.com.co", "notificaciones@boterosoto.com.co")]
    [InlineData("CORPORATIVO", "", "nvargas@boterosoto.com.co", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "", "nvargas@cualquiera.com", "nvargas@cualquiera.com")]
    // Validación #228: lo que MailAddress acepta pero VerificarCabeceras bloquearía sale con el corporativo (no falla el envío).
    [InlineData("comercial", "boterosoto.com.co", "\"n vargas\"@boterosoto.com.co", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "", "nvargas@intranet", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "", "nvargas@[10.0.0.1]", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co", "nvargas@boterosoto.com.co\r\nBcc: intruso@ejemplo.invalid", "notificaciones@boterosoto.com.co")]
    [InlineData("comercial", "boterosoto.com.co", "nvargas\u200B@boterosoto.com.co", "notificaciones@boterosoto.com.co")]
    public void AuditoriaElRemitenteSeEligePorModoYDominio(string modo, string dominios, string comercial, string esperado)
    {
        var opciones = new Cotifly.Infraestructura.Configuracion.clsOpcionesMailSender { ModoRemitente = modo, DominiosRemitente = dominios };

        opciones.ElegirRemitente(comercial).Should().Be(esperado);
    }

    [Fact]
    public async Task AuditoriaConElModoCorporativoElFromEsElRemitenteConfigurado()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso(), configuracionExtra: new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
            ["Correo:MailSender:ModoRemitente"] = "corporativo",
            ["Correo:MailSender:Remitente"] = "cotizaciones@boterosoto.com.co",
            ["Correo:Proteccion:BuzonPruebas"] = clsMailSenderFalso.BuzonPruebas,
        }, configurar: s => s.AddHttpClient(clsTransporteMailSender.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => _mailSender));
        using var ambito = proveedor.CreateScope();

        await ambito.ServiceProvider.GetRequiredService<IClsTransporteCorreo>().EnviarAsync(Mensaje(), Ct);

        _mailSender.Recibidos.Should().ContainSingle().Which.From.Should().Be("cotizaciones@boterosoto.com.co");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "datos inválidos (HTTP 400)")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "datos inválidos (HTTP 422)")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "tamaño permitido (HTTP 413)")]
    [InlineData(HttpStatusCode.InternalServerError, "no está disponible")]
    public async Task UnRechazoDeMailSenderEsUnEnvioFallidoConElMotivo(HttpStatusCode estado, string motivo)
    {
        _mailSender.AltaFalla = estado;

        var enviar = () => Transporte.EnviarAsync(Mensaje(), Ct);

        var error = (await enviar.Should().ThrowAsync<clsEnvioCorreoException>()).Which;
        error.Message.Should().Contain(motivo);
        error.Incierto.Should().BeFalse();
        _mailSender.Recibidos.Should().BeEmpty();
    }

    /// <summary>
    /// DEF-1027B-2 (#228): defensa en profundidad. Aunque el caso de uso ya lo rechaza, si algo con caracteres de control o una dirección
    /// mal formada llega al transporte (otro llamador, un dato viejo de la base), NO sale el POST multipart: excepción controlada, no
    /// incierta (MailSender no recibió nada).
    /// </summary>
    [Theory]
    [InlineData("asunto")]
    [InlineData("asunto_unicode")]
    [InlineData("para")]
    [InlineData("para_lista")]
    [InlineData("cc")]
    [InlineData("bcc")]
    [InlineData("adjunto")]
    [InlineData("correlacion")]
    public async Task DEF1027B2_UnaCabeceraInvalidaNoLlegaAMailSender(string campo)
    {
        var mensaje = Mensaje();
        mensaje = campo switch
        {
            "asunto" => mensaje with { Asunto = "Hola\r\nBcc: intruso@ejemplo.invalid" },
            "asunto_unicode" => mensaje with { Asunto = "Hola" + (char)0x2028 + "Bcc: intruso@ejemplo.invalid" },
            "para" => mensaje with { Para = ["cliente@empresa.com\r\nBcc: intruso@ejemplo.invalid"] },
            "para_lista" => mensaje with { Para = ["cliente@empresa.com,intruso@ejemplo.invalid"] },
            "cc" => mensaje with { Cc = ["jefe@empresa.com\n"] },
            "bcc" => mensaje with { Bcc = ["nvargas@boterosoto.com.co\0"] },
            "adjunto" => mensaje with { Adjunto = new clsAdjuntoCorreo("PCN-1\r\n.pdf", [1]) },
            _ => mensaje with { IdCorrelacion = "PCN-1\r\nX-Otra: 1" },
        };

        var enviar = () => Transporte.EnviarAsync(mensaje, Ct);

        var error = (await enviar.Should().ThrowAsync<clsEnvioCorreoException>()).Which;
        error.Incierto.Should().BeFalse();
        error.Message.Should().Contain(clsTransporteMailSender.MensajeCabeceraInvalida);
        _mailSender.Correlaciones.Should().BeEmpty("el POST multipart no sale");
    }

    [Fact]
    public async Task UnMailSenderCaidoEsUnEnvioFallidoSinReintentarElPost()
    {
        _mailSender.Caido = true;

        var enviar = () => Transporte.EnviarAsync(Mensaje(), Ct);

        (await enviar.Should().ThrowAsync<clsEnvioCorreoException>()).Which.Message.Should().Be(clsTransporteMailSender.MensajeNoDisponible);
        _mailSender.Correlaciones.Should().ContainSingle("el POST no se reintenta: MailSender no tiene clave de idempotencia");
    }

    [Fact]
    public async Task SinRespuestaATiempoElEnvioEsInciertoYNoSeReintenta()
    {
        _mailSender.Demora = TimeSpan.FromSeconds(5);

        var enviar = () => Transporte.EnviarAsync(Mensaje(), Ct);

        var error = (await enviar.Should().ThrowAsync<clsEnvioCorreoException>()).Which;
        error.Incierto.Should().BeTrue("MailSender pudo encolarlo");
        error.Message.Should().Be(clsTransporteMailSender.MensajeTiempoAgotado);
        _mailSender.Correlaciones.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0, EstadoCorreo.Encolado)]
    [InlineData(1, EstadoCorreo.Encolado)]
    [InlineData(2, EstadoCorreo.Enviando)]
    [InlineData(3, EstadoCorreo.Enviado)]
    [InlineData(4, EstadoCorreo.Enviando)]
    [InlineData(5, EstadoCorreo.Fallido)]
    [InlineData(6, EstadoCorreo.Fallido)]
    [InlineData(99, EstadoCorreo.Encolado)]
    public void LosEstadosDeMailSenderSeTraducenALosDeCotifly(int estado, EstadoCorreo esperado) =>
        clsTransporteMailSender.Traducir(estado).Should().Be(esperado);

    [Fact]
    public async Task LaConsultaTraeElEstadoElErrorYLosIntentos()
    {
        var aceptado = await Transporte.EnviarAsync(Mensaje(), Ct);
        var id = aceptado.IdExterno!.Value;

        (await Seguimiento.ConsultarAsync(id, "PCN-1:x", Ct))!.Estado.Should().Be(EstadoCorreo.Encolado);

        _mailSender.ProgramarReintento(id);
        var enviando = await Seguimiento.ConsultarAsync(id, "PCN-1:x", Ct);
        enviando!.Estado.Should().Be(EstadoCorreo.Enviando);
        enviando.Error.Should().Contain("421");

        _mailSender.Fallar(id, "550 5.7.1 Unable to relay", intentos: 5);
        var fallido = await Seguimiento.ConsultarAsync(id, "PCN-1:x", Ct);
        fallido.Should().Be(new clsEstadoEntregaCorreo(EstadoCorreo.Fallido, "550 5.7.1 Unable to relay", 5, null));

        _mailSender.Entregar(id, intentos: 6);
        var enviado = await Seguimiento.ConsultarAsync(id, "PCN-1:x", Ct);
        enviado!.Estado.Should().Be(EstadoCorreo.Enviado);
        enviado.Error.Should().BeNull();
        enviado.Intentos.Should().Be(6);
        enviado.EnviadoAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UnCorreoQueMailSenderNoConoceSeConsultaComoNullYNoSeReintenta()
    {
        (await Seguimiento.ConsultarAsync(Guid.NewGuid(), null, Ct)).Should().BeNull();
        (await Seguimiento.ReintentarAsync(Guid.NewGuid(), null, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task ElReintentoVuelveAEncolarElCorreo()
    {
        var id = (await Transporte.EnviarAsync(Mensaje(), Ct)).IdExterno!.Value;
        _mailSender.Fallar(id);

        (await Seguimiento.ReintentarAsync(id, "PCN-1:x", Ct)).Should().BeTrue();

        _mailSender.Reintentos.Should().Be(1);
        (await Seguimiento.ConsultarAsync(id, null, Ct))!.Estado.Should().Be(EstadoCorreo.Encolado);
    }

    [Fact]
    public async Task UnaConsultaConMailSenderCaidoLanzaElErrorDelTransporte()
    {
        _mailSender.Caido = true;

        var consultar = () => Seguimiento.ConsultarAsync(Guid.NewGuid(), null, Ct);

        await consultar.Should().ThrowAsync<clsEnvioCorreoException>();
    }

    [Fact]
    public void UnEstadoConUnFormatoInesperadoNoSeInterpreta()
    {
        clsTransporteMailSender.LeerEstado("{}").Should().BeNull();
        clsTransporteMailSender.LeerEstado("no es json").Should().BeNull();
        clsTransporteMailSender.LeerEstado("""{"status":"Sent","attemptCount":2}""")!.Estado.Should().Be(EstadoCorreo.Enviado);
        clsTransporteMailSender.LeerEstado("""{"status":6}""")!.Error.Should().Contain("canceló");
    }
}
