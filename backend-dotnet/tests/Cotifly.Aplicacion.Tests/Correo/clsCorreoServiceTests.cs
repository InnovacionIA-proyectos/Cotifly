using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Correo;

/// <summary>
/// Caso de uso <c>enviar_correo</c> con puertos simulados. Port de <c>tests/test_correo.py</c> (confirmación obligatoria,
/// cotización inexistente, PDF ausente, duplicado &lt; 5 min, distinto destinatario, fallo del transporte) más dueño,
/// correo deshabilitado, <c>reenviada</c> (bug del Python) y fila <c>fallido</c>. HU-033 (#199): MailSender encola (fila
/// <c>encolado</c> con su id), copia oculta al comercial, protección "redirigir", correlación, metadatos y el resumen con el total.
/// </summary>
public sealed class clsCorreoServiceTests : IDisposable
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCorreoRepository _correos = Substitute.For<IClsCorreoRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsTransporteCorreo _transporte = Substitute.For<IClsTransporteCorreo>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsCandadoEnvios _candado = new();
    private static readonly Guid _idMailSender = Guid.Parse("6d1a0f4e-3b2c-4c1d-9e8f-0a1b2c3d4e5f");
    private readonly clsOpcionesCorreo _opciones = new() { Habilitado = true, Proteccion = new() { Modo = "ninguno", PermitirEnvioReal = true } };
    private readonly clsLoggerFalso<clsCorreoService> _logger = new();
    private readonly clsUsuarioWeb _comercial;
    private readonly clsCotizacionSolicitud _cotizacion;
    private readonly List<clsCorreo> _registrados = [];
    private clsMensajeCorreo? _enviado;

    public clsCorreoServiceTests()
    {
        _comercial = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas", "nvargas@boterosoto.com.co", null, _reloj.AhoraUtc).Valor;
        _cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-C1",
            UsuarioId = _comercial.Id,
            NitCliente = "900123456",
            PdfPath = "/data/pdfs/PCN-C1.pdf",
        }).Valor;

        _usuarios.ObtenerPorIdAsync(_comercial.Id, Arg.Any<CancellationToken>()).Returns(_comercial);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C1", _comercial.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(_cotizacion.Id, _comercial.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidadTrabajo.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _almacen.LeerPorReferenciaAsync("/data/pdfs/PCN-C1.pdf", Arg.Any<CancellationToken>()).Returns("%PDF-1.4 prueba"u8.ToArray());
        _transporte.Modo.Returns("mailsender");
        _transporte.EnviarAsync(Arg.Do<clsMensajeCorreo>(m => _enviado = m), Arg.Any<CancellationToken>())
            .Returns(new clsEnvioAceptado(_idMailSender, EstadoCorreo.Encolado, null));
        _correos.When(c => c.Agregar(Arg.Any<clsCorreo>())).Do(ci => _registrados.Add(ci.Arg<clsCorreo>()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _candado.Dispose();

    private clsCorreoService Servicio() =>
        new(_cotizaciones, _correos, _usuarios, _almacen, _transporte, _unidadTrabajo, _reloj, _candado, Options.Create(_opciones), _logger);

    private clsEntradaEnviarCorreo Entrada(string[]? para = null, string[]? cc = null, Guid? usuario = null, string? asunto = null, string? cuerpo = null) =>
        new(usuario ?? _comercial.Id, para ?? ["cliente@empresa.com"], Folio: "PCN-C1", Cc: cc, Asunto: asunto, Cuerpo: cuerpo);

    // test_envio_ok_adjunta_pdf_y_persiste
    [Fact]
    public async Task ElEnvioAdjuntaElPdfSaleDelComercialYQuedaRegistrado()
    {
        var resultado = await Servicio().EnviarAsync(Entrada(cc: ["jefe@boterosoto.com.co"]), Ct);

        resultado.Valor.Enviado.Should().BeTrue();
        resultado.Valor.MessageId.Should().Be(_idMailSender.ToString());
        resultado.Valor.RegistradoEnBd.Should().BeTrue();
        resultado.Valor.Estado.Should().Be("encolado");
        resultado.Valor.RedirigidoA.Should().BeNull();
        _enviado!.Remitente.Should().Be("nvargas@boterosoto.com.co");
        _enviado.Para.Should().Equal("cliente@empresa.com");
        _enviado.Cc.Should().Equal("jefe@boterosoto.com.co");
        _enviado.Adjunto.Nombre.Should().Be("PCN-C1.pdf");
        _enviado.Adjunto.Contenido.Should().Equal("%PDF-1.4 prueba"u8.ToArray());
        _enviado.Asunto.Should().Be("Cotización PCN-C1 — Botero Soto");
        _enviado.CuerpoTexto.Should().Be(clsPlantillaCorreo.CuerpoPorDefecto);
        _enviado.CuerpoHtml.Should().Contain("Estimado cliente,<br>").And.Contain("Botero Soto</b> · Soluciones Logísticas");

        var fila = _registrados.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoCorreo.Encolado, "MailSender lo encoló (202): el worker confirma la entrega");
        fila.IdMailSender.Should().Be(_idMailSender);
        fila.Id.Should().Be(resultado.Valor.CorreoId);
        fila.SiguienteConsultaAt.Should().Be(_reloj.AhoraUtc + _opciones.Seguimiento.Intervalo);
        fila.ConfirmadoPor.Should().Be(_comercial.Id);
        fila.Para.Should().Equal("cliente@empresa.com");
        fila.Cc.Should().Equal("jefe@boterosoto.com.co");
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Enviada);
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElComercialVaEnCopiaOcultaYElCorreoLlevaCorrelacionYMetadatos()
    {
        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        _enviado!.Bcc.Should().Equal("nvargas@boterosoto.com.co");
        _enviado.IdCorrelacion.Should().Be($"PCN-C1:{resultado.Valor.CorreoId:D}");
        _enviado.Metadatos.Should().Contain("folio", "PCN-C1").And.Contain("origen", "cotifly")
            .And.Contain("correo_id", resultado.Valor.CorreoId.ToString("D")).And.Contain("cotizacion_id", _cotizacion.Id.ToString("D"));
    }

    [Theory]
    [InlineData("NVARGAS@boterosoto.com.co", null)]
    [InlineData("cliente@empresa.com", "nvargas@boterosoto.com.co")]
    public async Task SiElComercialYaVaEnParaOCcNoSeRepiteEnCopiaOculta(string para, string? cc)
    {
        await Servicio().EnviarAsync(Entrada(para: [para], cc: cc is null ? null : [cc]), Ct);

        _enviado!.Bcc.Should().BeEmpty();
    }

    [Fact]
    public async Task ConRedirigirTodoVaAlBuzonDePruebasConQaYLosDestinatariosOriginalesEnElCuerpo()
    {
        _opciones.Proteccion = new clsOpcionesProteccionCorreo { Modo = "redirigir", BuzonPruebas = "qa@pruebas.local" };

        var resultado = await Servicio().EnviarAsync(Entrada(para: ["cliente@empresa.com"], cc: ["jefe@empresa.com"]), Ct);

        _enviado!.Para.Should().Equal("qa@pruebas.local");
        _enviado.Cc.Should().BeEmpty("ningún CC real sale de un ambiente de pruebas");
        _enviado.Bcc.Should().BeEmpty("ni la copia oculta");
        _enviado.Asunto.Should().Be("[QA] Cotización PCN-C1 — Botero Soto");
        _enviado.CuerpoHtml.Should().Contain("Destinatarios originales")
            .And.Contain("cliente@empresa.com").And.Contain("jefe@empresa.com").And.Contain("nvargas@boterosoto.com.co");
        resultado.Valor.RedirigidoA.Should().Be("qa@pruebas.local");
        var fila = _registrados.Should().ContainSingle().Subject;
        fila.Para.Should().Equal("cliente@empresa.com");
        fila.Cc.Should().Equal("jefe@empresa.com");
        fila.RedirigidoA.Should().Be("qa@pruebas.local");
    }

    [Theory]
    [InlineData("ninguno", false)]
    [InlineData("redirigir", true)]
    [InlineData("lista_blanca", true)]
    [InlineData(null, false)]
    public async Task AuditoriaSinLasDosLlavesDelEnvioRealTodoVaAlBuzonDePruebas(string? modo, bool permitir)
    {
        // Auditoría #199: segura por defecto. "ninguno" sin PermitirEnvioReal (o la confirmación sin "ninguno", o un modo desconocido) redirige.
        _opciones.Proteccion = new clsOpcionesProteccionCorreo { Modo = modo, PermitirEnvioReal = permitir, BuzonPruebas = "qa@pruebas.local" };

        var resultado = await Servicio().EnviarAsync(Entrada(para: ["cliente@empresa.com"], cc: ["jefe@empresa.com"]), Ct);

        resultado.EsExitoso.Should().BeTrue();
        _enviado!.Para.Should().Equal("qa@pruebas.local");
        _enviado.Cc.Should().BeEmpty();
        _enviado.Bcc.Should().BeEmpty();
        _enviado.Metadatos.Values.Should().NotContain(v => v != null && v.Contains('@'), "la metadata no lleva destinatarios");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sin-arroba")]
    [InlineData("qa@pruebas.local,cliente@empresa.com")]
    [InlineData("\"qa pruebas\"@pruebas.local")] // Validación #228: MailAddress lo acepta; el transporte lo bloquearía
    [InlineData("qa@localhost")]
    public async Task AuditoriaSiHayQueRedirigirYNoHayBuzonValidoNoSaleNada(string? buzon)
    {
        // Falla cerrado: antes, sin buzón el mensaje salía SIN redirigir a los destinatarios reales.
        _opciones.Proteccion = new clsOpcionesProteccionCorreo { Modo = "ninguno", BuzonPruebas = buzon };

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
        resultado.Error.Mensaje.Should().Contain("BuzonPruebas");
        await NoSeEnvioNadaAsync();
        await _unidadTrabajo.DidNotReceive().GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditoriaUnEnvioInciertoConservaLaReservaYCuentaParaLaDeduplicacion()
    {
        _transporte.EnviarAsync(Arg.Any<clsMensajeCorreo>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new clsEnvioCorreoException("MailSender no respondió a tiempo") { Incierto = true });

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Enviada, "el correo pudo salir: no se deshace la reserva");
        var fila = _registrados.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoCorreo.Fallido);
        fila.Incierto.Should().BeTrue();
        fila.CuentaComoEnvio.Should().BeTrue("un reenvío inmediato a los mismos destinatarios sería un duplicado");
        fila.EsReintentable.Should().BeFalse();

        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([fila]);
        (await Servicio().EnviarAsync(Entrada(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CorreoDuplicado);
    }

    [Fact]
    public async Task ElCuerpoLlevaLaFirmaDelComercial()
    {
        await Servicio().EnviarAsync(Entrada(), Ct);

        _enviado!.CuerpoHtml.Should().Contain(System.Net.WebUtility.HtmlEncode("Nathaly Vargas · nvargas@boterosoto.com.co"));
    }

    [Fact]
    public async Task UnEnvioEnCursoEnMailSenderTambienCuentaParaLaDeduplicacion()
    {
        var previo = clsCorreo.Crear(_cotizacion.Id, ["cliente@empresa.com"], EstadoCorreo.Encolado, idMailSender: Guid.NewGuid(),
            siguienteConsultaAt: _reloj.AhoraUtc).Valor;
        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([previo]);

        (await Servicio().EnviarAsync(Entrada(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CorreoDuplicado);
    }

    [Fact]
    public async Task UnFallidoNoCuentaParaLaDeduplicacion()
    {
        var previo = clsCorreo.Crear(_cotizacion.Id, ["cliente@empresa.com"], EstadoCorreo.Fallido, idMailSender: Guid.NewGuid()).Valor;
        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([previo]);

        (await Servicio().EnviarAsync(Entrada(), Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task ElCuerpoLlevaElTotalDeLaCotizacionSinDesglose()
    {
        var costeada = clsCotizacionMuestra.Costeada(folio: "PCN-C2", usuarioId: _comercial.Id);
        costeada.Finalizar(_comercial.Id, clsCotizacionMuestra.Ahora); // HU-036.3 (#207): sale al cliente con el precio aceptado
        costeada.AsignarPdf("/data/pdfs/PCN-C2.pdf");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C2", _comercial.Id, Arg.Any<CancellationToken>()).Returns(costeada);
        _almacen.LeerPorReferenciaAsync("/data/pdfs/PCN-C2.pdf", Arg.Any<CancellationToken>()).Returns("%PDF-1.4"u8.ToArray());

        var resultado = await Servicio().EnviarAsync(new clsEntradaEnviarCorreo(_comercial.Id, ["cliente@empresa.com"], Folio: "PCN-C2"), Ct);

        resultado.EsExitoso.Should().BeTrue();
        var total = Cotifly.Aplicacion.Pdf.Services.clsFormatoMoneda.Formatear(costeada.PrecioFinal, "COP");
        _enviado!.CuerpoHtml.Should().Contain(System.Net.WebUtility.HtmlEncode(clsPlantillaCorreo.TituloResumen))
            .And.Contain(System.Net.WebUtility.HtmlEncode(clsPlantillaCorreo.EtiquetaTotal))
            .And.Contain(total + " COP").And.Contain(System.Net.WebUtility.HtmlEncode(Cotifly.Aplicacion.Pdf.Services.clsTextosEstandar.LeyendaImpuestos));
        _enviado.CuerpoHtml.Should().NotContainAny("Costo", "costo", "Margen", "margen", "Flete", "flete");
    }

    [Fact]
    public async Task HU0363_EnElFlujoDeCosteoSinFinalizarNoSeEnviaNiLlegaAMailSender()
    {
        var costeada = clsCotizacionMuestra.Costeada(folio: "PCN-C3", usuarioId: _comercial.Id);
        costeada.AsignarPdf("/data/pdfs/PCN-C3.pdf");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C3", _comercial.Id, Arg.Any<CancellationToken>()).Returns(costeada);
        _almacen.LeerPorReferenciaAsync("/data/pdfs/PCN-C3.pdf", Arg.Any<CancellationToken>()).Returns("%PDF-1.4"u8.ToArray());

        var resultado = await Servicio().EnviarAsync(new clsEntradaEnviarCorreo(_comercial.Id, ["cliente@empresa.com"], Folio: "PCN-C3"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        resultado.Error.Mensaje.Should().StartWith("Finaliza la cotización PCN-C3 antes de enviarla al cliente");
        _enviado.Should().BeNull("no llega a MailSender");
        costeada.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
    }

    [Fact]
    public async Task TambienSeEnviaPorIdDeCotizacion()
    {
        var entrada = new clsEntradaEnviarCorreo(_comercial.Id, ["cliente@empresa.com"], CotizacionId: _cotizacion.Id);

        (await Servicio().EnviarAsync(entrada, Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task ElAsuntoYElCuerpoDelComercialMandanYElHtmlLosEscapa()
    {
        await Servicio().EnviarAsync(Entrada(asunto: "Propuesta <urgente>", cuerpo: "Hola <b>equipo</b>\nSaludos"), Ct);

        _enviado!.Asunto.Should().Be("Propuesta <urgente>");
        _enviado.CuerpoTexto.Should().Be("Hola <b>equipo</b>\nSaludos");
        _enviado.CuerpoHtml.Should().Contain("Hola &lt;b&gt;equipo&lt;/b&gt;<br>Saludos");
    }

    [Fact]
    public async Task SinCorreoDelComercialElTransporteUsaSuRemitenteDeRespaldo()
    {
        var sinCorreo = clsUsuarioWeb.Crear("sincorreo", "Sin Correo", null, null, _reloj.AhoraUtc).Valor;
        _usuarios.ObtenerPorIdAsync(sinCorreo.Id, Arg.Any<CancellationToken>()).Returns(sinCorreo);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C1", sinCorreo.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);

        await Servicio().EnviarAsync(Entrada(usuario: sinCorreo.Id), Ct);

        _enviado!.Remitente.Should().BeNull();
        _enviado.Bcc.Should().BeEmpty("sin correo no hay copia oculta");
    }

    // test_sin_confirmado_por_rechaza_y_no_envia
    [Fact]
    public async Task SinComercialAutenticadoNoSeEnvia()
    {
        var resultado = await Servicio().EnviarAsync(Entrada(usuario: Guid.Empty), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
        await NoSeEnvioNadaAsync();
    }

    [Fact]
    public async Task ConElCorreoDeshabilitadoNoSeEnvia()
    {
        _opciones.Habilitado = false;

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoDeshabilitado);
        await NoSeEnvioNadaAsync();
    }

    // test_cotizacion_no_encontrada + aislamiento por dueño (HU-003.6)
    [Fact]
    public async Task UnaCotizacionAjenaOInexistenteNoSeEnvia()
    {
        var otro = Guid.NewGuid();

        var resultado = await Servicio().EnviarAsync(Entrada(usuario: otro), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await NoSeEnvioNadaAsync();
    }

    /// <summary>
    /// Endurecimiento A (#199): el dueño se comprueba primero. Un folio ajeno o inexistente es <c>COTIZACION_NO_ENCONTRADA</c> (404) aunque el
    /// correo esté deshabilitado (503) o los destinatarios sean inválidos (400): si no, esas respuestas dejarían enumerar folios.
    /// </summary>
    [Theory]
    [InlineData(false, "cliente@empresa.com")]
    [InlineData(true, "no-es-correo")]
    [InlineData(false, "no-es-correo")]
    public async Task UnaCotizacionAjenaEsNoEncontradaAntesQueCorreoDeshabilitadoOInvalido(bool habilitado, string para)
    {
        _opciones.Habilitado = habilitado;

        var resultado = await Servicio().EnviarAsync(Entrada(para: [para], usuario: Guid.NewGuid()), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await NoSeEnvioNadaAsync();
    }

    // test_pdf_no_encontrado
    [Fact]
    public async Task SinPdfEnElAlmacenNoSeEnvia()
    {
        _almacen.LeerPorReferenciaAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoPdfNoEncontrado);
        await NoSeEnvioNadaAsync();
    }

    // test_duplicado_menos_5min_rechaza
    [Fact]
    public async Task UnEnvioALosMismosDestinatariosEnMenosDeCincoMinutosEsDuplicado()
    {
        var previo = clsCorreo.Crear(_cotizacion.Id, ["cliente@empresa.com", "b@x.com"], EstadoCorreo.Enviado).Valor;
        _correos.ListarEnviadosDesdeAsync(_cotizacion.Id, _reloj.AhoraUtc - TimeSpan.FromMinutes(5), Arg.Any<CancellationToken>()).Returns([previo]);

        var resultado = await Servicio().EnviarAsync(Entrada(para: ["B@X.com", " CLIENTE@empresa.com "]), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoDuplicado);
        await NoSeEnvioNadaAsync();
    }

    // test_distintos_destinatarios_no_es_duplicado
    [Fact]
    public async Task OtroConjuntoDeDestinatariosNoEsDuplicado()
    {
        var previo = clsCorreo.Crear(_cotizacion.Id, ["a@x.com"], EstadoCorreo.Enviado).Valor;
        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([previo]);

        (await Servicio().EnviarAsync(Entrada(para: ["b@y.com"]), Ct)).EsExitoso.Should().BeTrue();
    }

    // test_duplicado_mas_5min_permite: la ventana se pide al repositorio con el reloj de la aplicación.
    [Fact]
    public async Task LaVentanaDeDeduplicacionSeMideConElRelojDeLaAplicacion()
    {
        _reloj.Avanzar(TimeSpan.FromHours(3));

        await Servicio().EnviarAsync(Entrada(), Ct);

        await _correos.Received(1).ListarEnviadosDesdeAsync(_cotizacion.Id, _reloj.AhoraUtc - clsCorreoService.VentanaDuplicado, Arg.Any<CancellationToken>());
    }

    // test_fallo_smtp_eleva_envio_smtp_error + fila "fallido" (server.py)
    [Fact]
    public async Task UnFalloDelTransporteQuedaRegistradoComoFallidoYNoCambiaElEstado()
    {
        _transporte.EnviarAsync(Arg.Any<clsMensajeCorreo>(), Arg.Any<CancellationToken>()).ThrowsAsync(new clsEnvioCorreoException("Relay SMTP 10.10.40.62 falló: conexión rechazada"));

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
        resultado.Error.Mensaje.Should().Contain("conexión rechazada");
        var fila = _registrados.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoCorreo.Fallido);
        fila.Error.Should().Contain("conexión rechazada");
        fila.MessageId.Should().BeNull();
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElEnvioReservaElEstadoConLaVersionLeidaAntesDeQueSalgaElCorreo()
    {
        var versiones = new List<int>();
        _unidadTrabajo.GuardarCambiosSiVersionAsync(_cotizacion.Id, Arg.Do<int>(versiones.Add), Arg.Any<CancellationToken>()).Returns(true);

        (await Servicio().EnviarAsync(Entrada(), Ct)).Valor.Enviado.Should().BeTrue();

        versiones.Should().Equal(0);
        _cotizacion.VersionNegocio.Should().Be(1, "el envío es un cambio de negocio");
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Enviada);
    }

    [Fact]
    public async Task SiOtroFlujoCambioLaCotizacionElCorreoNoSaleYRespondeConflicto()
    {
        _unidadTrabajo.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _transporte.DidNotReceiveWithAnyArgs().EnviarAsync(default!, Ct);
    }

    [Fact]
    public async Task SiNoSePuedeRegistrarElFallidoIgualSeInformaElFallo()
    {
        _transporte.EnviarAsync(Arg.Any<clsMensajeCorreo>(), Arg.Any<CancellationToken>()).ThrowsAsync(new clsEnvioCorreoException("caído"));
        _unidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
    }

    // Carrera de estadoEnvio (#11): la cotización se recarga DENTRO del candado, antes de registrar el envío.
    [Fact]
    public async Task LaCotizacionSeRecargaDentroDelCandadoAntesDeEnviar()
    {
        await Servicio().EnviarAsync(Entrada(), Ct);

        Received.InOrder(() =>
        {
            _cotizaciones.RecargarAsync(_cotizacion, Arg.Any<CancellationToken>());
            _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            _transporte.EnviarAsync(Arg.Any<clsMensajeCorreo>(), Arg.Any<CancellationToken>());
        });
    }

    // Bug del Python: estado_envio nunca pasaba a "reenviada".
    [Fact]
    public async Task UnSegundoEnvioExitosoDejaLaCotizacionReenviada()
    {
        await Servicio().EnviarAsync(Entrada(para: ["a@x.com"]), Ct);
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Enviada);

        await Servicio().EnviarAsync(Entrada(para: ["b@x.com"]), Ct);
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Reenviada);

        await Servicio().EnviarAsync(Entrada(para: ["c@x.com"]), Ct);
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Reenviada);
    }

    [Fact]
    public async Task UnRechazoDeMailSenderDejaLaFilaFallidaSinIdYDeshaceLaReserva()
    {
        _transporte.EnviarAsync(Arg.Any<clsMensajeCorreo>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new clsEnvioCorreoException("MailSender rechazó el correo por datos inválidos (HTTP 422)."));

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
        var fila = _registrados.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoCorreo.Fallido);
        fila.IdMailSender.Should().BeNull();
        fila.EsReintentable.Should().BeFalse("nunca llegó a MailSender: se envía de nuevo");
        fila.SiguienteConsultaAt.Should().BeNull();
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
    }

    [Fact]
    public async Task SiElCorreoSalioPeroNoSePudoRegistrarSeInformaEnviadoSinRegistro()
    {
        _unidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var resultado = await Servicio().EnviarAsync(Entrada(), Ct);

        resultado.Valor.Enviado.Should().BeTrue();
        resultado.Valor.RegistradoEnBd.Should().BeFalse();
        _logger.Registros.Should().Contain(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "sin-arroba" }, null)]
    [InlineData(new[] { "con espacio@x.com" }, null)]
    [InlineData(new[] { "ok@x.com" }, "cc-invalido")]
    public async Task DestinatariosInvalidosSeRechazanSinEnviar(string[] para, string? cc)
    {
        var resultado = await Servicio().EnviarAsync(Entrada(para: para, cc: cc is null ? null : [cc]), Ct);

        resultado.Error.Codigo.Should().Be(clsCorreo.CodigoError);
        await NoSeEnvioNadaAsync();
    }

    [Fact]
    public async Task UnAsuntoOCuerpoDemasiadoLargoSeRechaza()
    {
        (await Servicio().EnviarAsync(Entrada(asunto: new string('a', 301)), Ct)).Error.Codigo.Should().Be(clsCorreo.CodigoError);
        (await Servicio().EnviarAsync(Entrada(cuerpo: new string('a', 5001)), Ct)).Error.Codigo.Should().Be(clsCorreo.CodigoError);
    }

    // DEF-1027B-2 (#228, E2E real del 2026-10-27B): "Hola\r\nBcc: intruso@ejemplo.invalid" llegó a MailSender y al relay SMTP. Un asunto
    // con caracteres de control se RECHAZA (no se sanea en silencio), también al final (antes, el Trim lo limpiaba).
    [Theory]
    [InlineData("Hola\r\nBcc: intruso@ejemplo.invalid")]
    [InlineData("Hola\nBcc: intruso@ejemplo.invalid")]
    [InlineData("Hola\rmundo")]
    [InlineData("Hola\0mundo")]
    [InlineData("Hola\tmundo")]
    [InlineData("Cotización PCN-C1\r\n")]
    public async Task DEF1027B2_UnAsuntoConCaracteresDeControlSeRechazaSinEnviar(string asunto)
    {
        var resultado = await Servicio().EnviarAsync(Entrada(asunto: asunto), Ct);

        resultado.Error.Codigo.Should().Be(clsCorreo.CodigoError);
        resultado.Error.Mensaje.Should().Be(clsCorreoService.MensajeAsuntoConControl);
        await NoSeEnvioNadaAsync();
        _cotizacion.EstadoEnvio.Should().NotBe(EstadoEnvio.Enviada);
    }

    [Theory]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x007F)]
    public async Task DEF1027B2_TambienSeRechazanLosSaltosUnicodeYLosDemasControles(int caracter)
    {
        var resultado = await Servicio().EnviarAsync(Entrada(asunto: "Hola" + (char)caracter + "Bcc: intruso@ejemplo.invalid"), Ct);

        resultado.Error.Codigo.Should().Be(clsCorreo.CodigoError);
        await NoSeEnvioNadaAsync();
    }

    [Theory]
    [InlineData(new[] { "cliente@empresa.com\r\nBcc: intruso@ejemplo.invalid" }, null)]
    [InlineData(new[] { "cliente@empresa.com\r\n" }, null)]
    [InlineData(new[] { "\r\n" }, null)]
    [InlineData(new[] { "cliente@empresa.com" }, "jefe@empresa.com\nBcc: intruso@ejemplo.invalid")]
    [InlineData(new[] { "cliente@empresa.com" }, "jefe@empresa.com\0")]
    [InlineData(new[] { "cliente@empresa.com" }, "jefe@localhost")]
    [InlineData(new[] { "cliente@empresa.com, intruso@ejemplo.invalid" }, null)]
    public async Task DEF1027B2_UnaDireccionConControlesOMalFormadaSeRechazaSinEnviar(string[] para, string? cc)
    {
        var resultado = await Servicio().EnviarAsync(Entrada(para: para, cc: cc is null ? null : [cc]), Ct);

        resultado.Error.Codigo.Should().Be(clsCorreo.CodigoError);
        await NoSeEnvioNadaAsync();
    }

    // #228: MailSender no limita los destinatarios (EmailRequestValidator solo valida el formato): Cotifly fija el tope entre para y cc,
    // 10 por defecto y configurable con Correo__MaximoDestinatarios (decisión del 2026-10-27).
    [Fact]
    public void Tope228_ElTopePorDefectoEs10()
    {
        new clsOpcionesCorreo().MaximoDestinatarios.Should().Be(10);
        clsOpcionesCorreo.MaximoDestinatariosPorDefecto.Should().Be(10);
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(6, 5)]
    [InlineData(60, 0)]
    public async Task Tope228_MasDe10DestinatariosEntreParaYCcSeRechazaConDetalle(int nPara, int nCc)
    {
        var resultado = await Servicio().EnviarAsync(Entrada(para: Direcciones("cliente", nPara), cc: Direcciones("copia", nCc)), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoDemasiadosDestinatarios);
        resultado.Error.Datos!["maximo"].Should().Be(10);
        resultado.Error.Datos["recibidos"].Should().Be(nPara + nCc);
        await NoSeEnvioNadaAsync();
    }

    [Fact]
    public async Task Tope228_Con10DestinatariosEntreParaYCcSiSeEnviaYLaCopiaOcultaNoCuenta()
    {
        var resultado = await Servicio().EnviarAsync(Entrada(para: Direcciones("cliente", 6), cc: Direcciones("copia", 4)), Ct);

        resultado.Valor.Enviado.Should().BeTrue();
        _enviado!.Para.Should().HaveCount(6);
        _enviado.Cc.Should().HaveCount(4);
        _enviado.Bcc.Should().ContainSingle("la copia oculta al comercial va aparte y no cuenta en el tope");
    }

    [Theory]
    [InlineData(3, 4, false)]
    [InlineData(3, 3, true)]
    [InlineData(25, 25, true)]
    [InlineData(25, 26, false)]
    public async Task Tope228_ElTopeConfiguradoMandaYSaleEnElDetalle(int maximo, int recibidos, bool seEnvia)
    {
        _opciones.MaximoDestinatarios = maximo;

        var resultado = await Servicio().EnviarAsync(Entrada(para: Direcciones("cliente", recibidos)), Ct);

        if (seEnvia)
        {
            resultado.Valor.Enviado.Should().BeTrue();
            _enviado!.Para.Should().HaveCount(recibidos);
        }
        else
        {
            resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoDemasiadosDestinatarios);
            resultado.Error.Datos!["maximo"].Should().Be(maximo);
            resultado.Error.Datos["recibidos"].Should().Be(recibidos);
            resultado.Error.Mensaje.Should().Contain($"hasta {maximo} destinatarios");
            await NoSeEnvioNadaAsync();
        }
    }

    private static string[] Direcciones(string prefijo, int cantidad) =>
        [.. Enumerable.Range(1, cantidad).Select(i => $"{prefijo}{i}@empresa.com")];

    private async Task NoSeEnvioNadaAsync()
    {
        await _transporte.DidNotReceiveWithAnyArgs().EnviarAsync(default!, Ct);
        _registrados.Should().BeEmpty();
    }
}
