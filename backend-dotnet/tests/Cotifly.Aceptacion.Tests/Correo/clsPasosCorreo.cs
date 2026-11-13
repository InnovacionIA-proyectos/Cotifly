using System.Net;
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
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Correo;

/// <summary>
/// Pasos de <c>specs/features/correo.feature</c> (y los de envío que reutiliza <c>auditoria.feature</c>). El transporte es el
/// MailSender falso del escenario (<see cref="clsContextoEscenario.MailSender"/>); nunca sale un correo real.
/// </summary>
[Binding]
public sealed class clsPasosCorreo(clsContextoEscenario contexto)
{
    private clsDestinatarios? _destinatarios;
    private clsVistaPreviaCorreo? _vistaPrevia;
    private int _correosAntesDeLaVista;
    private clsErrorDominio? _errorReintento;

    [Given("el envío de correo habilitado con el MailSender de pruebas")]
    public void DadoElCorreoHabilitado() =>
        contexto.Reconfigurar(new Dictionary<string, string?> { ["Correo:Habilitado"] = "true" });

    [Given("el ambiente de producción sin protección de correo")]
    public void DadoProduccionSinProteccion() =>
        contexto.UsarEntorno("Production", new Dictionary<string, string?> { ["Correo:Habilitado"] = "true", ["Correo:Proteccion:Modo"] = "ninguno", ["Correo:Proteccion:PermitirEnvioReal"] = "true" });

    [Given("el ambiente de producción con la protección de correo por defecto")]
    public void DadoProduccionConProteccion() =>
        contexto.UsarEntorno("Production", new Dictionary<string, string?> { ["Correo:Habilitado"] = "true", ["Correo:Proteccion:Modo"] = "redirigir", ["Correo:Proteccion:PermitirEnvioReal"] = "false" });

    [Given("MailSender no responde a tiempo al encolar")]
    public void DadoMailSenderLento()
    {
        // El POST se corta por tiempo (1 s) mientras MailSender tarda 3 s: pudo quedar encolado, el resultado es incierto.
        contexto.Reconfigurar(new Dictionary<string, string?> { ["Correo:MailSender:TimeoutSegundos"] = "1" });
        contexto.MailSender.Demora = TimeSpan.FromSeconds(3);
    }

    [When("MailSender vuelve a responder a tiempo")]
    public void CuandoMailSenderVuelve() => contexto.MailSender.Demora = null;

    [Then("el último correo de {string} queda registrado como incierto")]
    public async Task EntoncesElUltimoCorreoQuedaIncierto(string folio)
    {
        var correo = await UltimoCorreoAsync(folio);
        correo.Incierto.Should().BeTrue("sin respuesta al POST, MailSender pudo encolarlo");
        clsCodigoEnum.ACodigo(correo.Estado!.Value).Should().Be("fallido");
        correo.IdMailSender.Should().BeNull();
        correo.Error.Should().StartWith(Dominio.Correo.clsCorreo.PrefijoIncierto);
    }

    [Given("el envío de correo deshabilitado")]
    public void DadoElCorreoDeshabilitado() => contexto.Reconfigurar(new Dictionary<string, string?> { ["Correo:Habilitado"] = "false" });

    [Given("MailSender rechaza los correos con el estado {int}")]
    public void DadoMailSenderRechaza(int estado) => contexto.MailSender.AltaFalla = (HttpStatusCode)estado;

    [When("MailSender entrega el último correo")]
    public void CuandoMailSenderEntrega() => contexto.MailSender.Entregar(contexto.MailSender.Recibidos[^1].Id);

    [When("MailSender no logra entregar el último correo por {string}")]
    public void CuandoMailSenderFalla(string error) => contexto.MailSender.Fallar(contexto.MailSender.Recibidos[^1].Id, error);

    [When("pasa {int} minuto y Cotifly consulta los correos de {string}")]
    public async Task CuandoCotiflyConsulta(int minutos, string folio)
    {
        contexto.Reloj.Avanzar(TimeSpan.FromMinutes(minutos));
        (await contexto.ProcesarCorreosAsync(folio)).Should().BeGreaterThan(0);
    }

    [When("{string} reintenta el último correo de {string}")]
    public async Task CuandoReintenta(string nombreUsuario, string folio)
    {
        var ultimo = await UltimoCorreoAsync(folio);
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsHistorialCorreosService>().ReintentarAsync(Usuario(nombreUsuario), folio, ultimo.Id));
        _errorReintento = resultado.EsFallo ? resultado.Error : null;
    }

    [Then("el reintento es exitoso")]
    public void EntoncesElReintentoEsExitoso() => _errorReintento.Should().BeNull(_errorReintento?.Mensaje);

    [Then("el reintento se rechaza con el código {string}")]
    public void EntoncesElReintentoSeRechaza(string codigo) => _errorReintento!.Codigo.Should().Be(codigo);

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

    [Then("MailSender recibe el correo de {string} para {string} con copia a {string} y copia oculta a {string}")]
    public void EntoncesMailSenderRecibe(string remitente, string para, string cc, string bcc)
    {
        var recibido = contexto.MailSender.Recibidos.Should().ContainSingle().Subject;
        recibido.From.Should().Be(remitente);
        recibido.To.Should().Equal(para);
        recibido.Cc.Should().Equal(cc);
        recibido.Bcc.Should().Equal(bcc);
        recibido.IsHtml.Should().BeTrue();
    }

    [Then("MailSender recibe el correo solo para el buzón de pruebas con el asunto {string}")]
    public void EntoncesMailSenderRecibeEnElBuzon(string asunto)
    {
        var recibido = contexto.MailSender.Recibidos.Should().ContainSingle().Subject;
        recibido.To.Should().Equal(clsMailSenderFalso.BuzonPruebas);
        recibido.Cc.Should().BeEmpty();
        recibido.Bcc.Should().BeEmpty();
        recibido.Subject.Should().Be(asunto);
    }

    [Then("el cuerpo del correo lista los destinatarios originales {string}, {string} y {string}")]
    public void EntoncesElCuerpoLista(string para, string cc, string bcc) =>
        contexto.MailSender.Recibidos[^1].Body.Should().Contain("Destinatarios originales").And.Contain(para).And.Contain(cc).And.Contain(bcc);

    [Then("el correo adjunta {string}")]
    public void EntoncesElCorreoAdjunta(string adjunto) =>
        contexto.MailSender.Recibidos[^1].Adjuntos.Select(a => a.Nombre).Should().Equal(adjunto);

    [Then("el correo tiene el asunto {string} y adjunta {string}")]
    public void EntoncesElCorreoTiene(string asunto, string adjunto)
    {
        contexto.MailSender.Recibidos[^1].Subject.Should().Be(asunto);
        EntoncesElCorreoAdjunta(adjunto);
    }

    [Then("MailSender no recibe ningún correo")]
    public void EntoncesMailSenderNoRecibe() => contexto.CorreosRecibidos.Should().Be(0);

    [Then("el último correo de {string} queda registrado como {string}")]
    public async Task EntoncesElUltimoCorreoQueda(string folio, string estado) =>
        clsCodigoEnum.ACodigo((await UltimoCorreoAsync(folio)).Estado!.Value).Should().Be(estado);

    [Then("el último correo de {string} queda registrado como {string} con el error {string}")]
    public async Task EntoncesElUltimoCorreoQuedaConError(string folio, string estado, string error)
    {
        var correo = await UltimoCorreoAsync(folio);
        clsCodigoEnum.ACodigo(correo.Estado!.Value).Should().Be(estado);
        correo.Error.Should().Be(error);
    }

    [Then("el último correo de {string} queda registrado como {string} para {string} y redirigido al buzón de pruebas")]
    public async Task EntoncesElUltimoCorreoQuedaRedirigido(string folio, string estado, string para)
    {
        var correo = await UltimoCorreoAsync(folio);
        clsCodigoEnum.ACodigo(correo.Estado!.Value).Should().Be(estado);
        correo.Para.Should().Equal(para);
        correo.RedirigidoA.Should().Be(clsMailSenderFalso.BuzonPruebas);
        correo.IdMailSender.Should().Be(contexto.MailSender.Recibidos[^1].Id);
    }

    [Then("la vista previa no anuncia la copia oculta porque al redirigir el correo sale sin CC ni CCO")]
    public void EntoncesLaVistaPreviaNoAnunciaCopiaOculta()
    {
        _vistaPrevia!.CopiaOculta.Should().BeNull();
        _vistaPrevia.Aviso.Should().Contain("sin CC ni CCO");
    }

    [Then("la vista previa avisa que es un ambiente de pruebas")]
    public void EntoncesLaVistaPreviaAvisa()
    {
        _vistaPrevia!.EntornoPruebas.Should().BeTrue();
        _vistaPrevia.RedirigidoA.Should().Be(clsMailSenderFalso.BuzonPruebas);
        _vistaPrevia.Aviso.Should().Contain("Ambiente de pruebas");
    }

    private async Task<Dominio.Correo.clsCorreo> UltimoCorreoAsync(string folio)
    {
        var correos = await contexto.ConsultarAsync(async s =>
        {
            var cotizacion = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio);
            return await s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion!.Id);
        });
        return correos[0];
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
