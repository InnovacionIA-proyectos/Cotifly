using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Correo;

/// <summary>Pasos de <c>specs/features/correo.feature</c> (y los de envío que reutiliza <c>auditoria.feature</c>).</summary>
[Binding]
public sealed class clsPasosCorreo(clsContextoEscenario contexto)
{
    private clsDestinatarios? _destinatarios;
    private clsVistaPreviaCorreo? _vistaPrevia;
    private int _correosAntesDeLaVista;

    [Given("el envío de correo habilitado con el relay SMTP de pruebas")]
    public void DadoElCorreoHabilitado() =>
        contexto.Reconfigurar(new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:Modo"] = "smtp_relay",
            ["Correo:SmtpRelay:Host"] = clsServidorSmtpFalso.Host,
            ["Correo:SmtpRelay:Port"] = contexto.Relay.Puerto.ToString(CultureInfo.InvariantCulture),
            ["Correo:SmtpRelay:TimeoutSeg"] = "10",
        });

    [Given("el envío de correo deshabilitado")]
    public void DadoElCorreoDeshabilitado() => contexto.Reconfigurar(new Dictionary<string, string?> { ["Correo:Habilitado"] = "false" });

    [Given("el relay rechaza los destinatarios")]
    public void DadoElRelayRechaza() => contexto.Relay.RechazarDestinatarios = true;

    [Given("que {string} tiene la cotización {string} del cliente {string} con su PDF")]
    public Task DadoLaCotizacionConPdf(string nombreUsuario, string folio, string nit) => CrearCotizacionAsync(nombreUsuario, folio, nit, conPdf: true);

    [Given("que {string} tiene la cotización {string} del cliente {string} sin PDF")]
    public Task DadoLaCotizacionSinPdf(string nombreUsuario, string folio, string nit) => CrearCotizacionAsync(nombreUsuario, folio, nit, conPdf: false);

    [When("{string} envía la cotización {string} a {string}")]
    public Task CuandoEnvia(string nombreUsuario, string folio, string para) => EnviarAsync(nombreUsuario, folio, para, null, null);

    [When("{string} envía la cotización {string} a {string} con copia a {string}")]
    public Task CuandoEnviaConCopia(string nombreUsuario, string folio, string para, string cc) => EnviarAsync(nombreUsuario, folio, para, cc, null);

    [When("{string} envía la cotización {string} a {string} en el hilo {string}")]
    public Task CuandoEnviaEnElHilo(string nombreUsuario, string folio, string para, string hilo) => EnviarAsync(nombreUsuario, folio, para, null, hilo);

    [Given("que {string} envió la cotización {string} a {string}")]
    public async Task DadoQueEnvio(string nombreUsuario, string folio, string para)
    {
        await EnviarAsync(nombreUsuario, folio, para, null, null);
        EntoncesElEnvioEsExitoso();
    }

    [Given("que {string} envió la cotización {string} a {string} con copia a {string}")]
    public async Task DadoQueEnvioConCopia(string nombreUsuario, string folio, string para, string cc)
    {
        await EnviarAsync(nombreUsuario, folio, para, cc, null);
        EntoncesElEnvioEsExitoso();
    }

    [When("pasan {int} minutos")]
    public void CuandoPasanMinutos(int minutos) => contexto.Reloj.Avanzar(TimeSpan.FromMinutes(minutos));

    [Then("el envío es exitoso")]
    public void EntoncesElEnvioEsExitoso() =>
        contexto.UltimoErrorNegocio.Should().BeNull(contexto.UltimoErrorNegocio?.Mensaje);

    [Then("el envío se rechaza con el código {string}")]
    public void EntoncesSeRechaza(string codigo)
    {
        contexto.UltimoErrorNegocio.Should().NotBeNull("se esperaba el rechazo {0}", codigo);
        contexto.UltimoErrorNegocio!.Codigo.Should().Be(codigo);
    }

    [Then("el relay recibe el correo de {string} para {string} y {string}")]
    public void EntoncesElRelayRecibe(string remitente, string para, string cc)
    {
        var recibido = contexto.Relay.Recibidos.Should().ContainSingle().Subject;
        recibido.Remitente.Should().Be(remitente);
        recibido.Destinatarios.Should().Equal(para, cc);
    }

    [Then("el correo tiene el asunto {string} y adjunta {string}")]
    public void EntoncesElCorreoTiene(string asunto, string adjunto)
    {
        using var mensaje = MimeMessage.Load(new MemoryStream(contexto.Relay.Recibidos[^1].Datos), CancellationToken.None);
        mensaje.Subject.Should().Be(asunto);
        mensaje.Attachments.OfType<MimePart>().Select(a => a.FileName).Should().Equal(adjunto);
    }

    [Then("el relay no recibe ningún correo")]
    public void EntoncesElRelayNoRecibe() => contexto.CorreosRecibidos.Should().Be(0);

    [Then("el último correo de {string} queda registrado como {string}")]
    public async Task EntoncesElUltimoCorreoQueda(string folio, string estado)
    {
        var correos = await contexto.ConsultarAsync(async s =>
        {
            var cotizacion = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio);
            return await s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion!.Id);
        });

        clsCodigoEnum.ACodigo(correos[0].Estado!.Value).Should().Be(estado);
    }

    [Then("la cotización {string} queda en estado de envío {string}")]
    public async Task EntoncesLaCotizacionQuedaEn(string folio, string estado)
    {
        var cotizacion = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio));
        clsCodigoEnum.ACodigo(cotizacion!.EstadoEnvio!.Value).Should().Be(estado);
    }

    [When("{string} consulta los destinatarios previos del cliente {string}")]
    public async Task CuandoConsultaDestinatarios(string nombreUsuario, string nit) =>
        _destinatarios = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsDestinatariosPreviosService>().ObtenerAsync(Usuario(nombreUsuario), nit));

    [Then("los destinatarios previos son {string} con copia a {string}")]
    public void EntoncesLosDestinatariosSon(string para, string cc)
    {
        _destinatarios!.Para.Should().Equal(para);
        _destinatarios.Cc.Should().Equal(cc);
    }

    [Then("no hay destinatarios previos")]
    public void EntoncesNoHayDestinatarios()
    {
        _destinatarios!.Para.Should().BeEmpty();
        _destinatarios.Cc.Should().BeEmpty();
    }

    [When("{string} pide la vista previa del correo de {string}")]
    public async Task CuandoPideLaVistaPrevia(string nombreUsuario, string folio)
    {
        _correosAntesDeLaVista = contexto.CorreosRecibidos;
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsVistaPreviaCorreoService>().PrevisualizarAsync(Usuario(nombreUsuario), folio));
        _vistaPrevia = resultado.Valor;
    }

    [Then("la vista previa tiene el asunto {string} y adjunta {string}")]
    public void EntoncesLaVistaPreviaTiene(string asunto, string adjunto)
    {
        _vistaPrevia!.Asunto.Should().Be(asunto);
        _vistaPrevia.Adjunto.Should().Be(adjunto);
        _vistaPrevia.Cuerpo.Should().Be(clsPlantillaCorreo.CuerpoPorDefecto);
    }

    [Then("la vista previa trae precargado {string}")]
    public void EntoncesLaVistaPreviaTraePrecargado(string para) => _vistaPrevia!.Para.Should().Equal(para);

    [Then("la vista previa no envía ningún correo")]
    public void EntoncesLaVistaPreviaNoEnvia() => contexto.CorreosRecibidos.Should().Be(_correosAntesDeLaVista);

    private async Task CrearCotizacionAsync(string nombreUsuario, string folio, string nit, bool conPdf)
    {
        var ruta = conPdf
            ? await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().GuardarNuevoAsync(folio + ".pdf", "%PDF-1.4 cotización de prueba"u8.ToArray()))
            : $"/data/pdfs/{folio}.pdf";
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            UsuarioId = Usuario(nombreUsuario),
            NitCliente = nit,
            ClienteNombre = "Cliente de prueba",
            PdfPath = ruta,
        }).Valor;
        await contexto.EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCotizacionRepository>().Agregar(cotizacion);
            return Task.CompletedTask;
        });
    }

    private async Task EnviarAsync(string nombreUsuario, string folio, string para, string? cc, string? hilo)
    {
        var entrada = new clsEntradaEnviarCorreo(Usuario(nombreUsuario), [para], Folio: folio, Cc: cc is null ? null : [cc], ThreadId: hilo);
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsCorreoService>().EnviarAsync(entrada));
        contexto.UltimoErrorNegocio = resultado.EsFallo ? resultado.Error : null;
    }

    private Guid Usuario(string nombreUsuario) =>
        contexto.Usuarios.TryGetValue(nombreUsuario, out var id)
            ? id
            : throw new InvalidOperationException($"El comercial '{nombreUsuario}' no está registrado en el escenario.");
}
