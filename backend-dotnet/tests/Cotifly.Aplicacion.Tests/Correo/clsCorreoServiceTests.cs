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
/// correo deshabilitado, <c>reenviada</c> (bug del Python) y fila <c>fallido</c>.
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
    private readonly clsOpcionesCorreo _opciones = new() { Habilitado = true };
    private readonly clsLoggerFalso<clsCorreoService> _logger = new();
    private readonly clsUsuarioWeb _comercial;
    private readonly clsCotizacion _cotizacion;
    private readonly List<clsCorreo> _registrados = [];
    private clsMensajeCorreo? _enviado;

    public clsCorreoServiceTests()
    {
        _comercial = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas", "nvargas@boterosoto.com.co", null, _reloj.AhoraUtc).Valor;
        _cotizacion = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-C1",
            UsuarioId = _comercial.Id,
            NitCliente = "900123456",
            PdfPath = "/data/pdfs/PCN-C1.pdf",
        }).Valor;

        _usuarios.ObtenerPorIdAsync(_comercial.Id, Arg.Any<CancellationToken>()).Returns(_comercial);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C1", _comercial.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(_cotizacion.Id, _comercial.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _almacen.LeerPorReferenciaAsync("/data/pdfs/PCN-C1.pdf", Arg.Any<CancellationToken>()).Returns("%PDF-1.4 prueba"u8.ToArray());
        _transporte.Modo.Returns("smtp_relay");
        _transporte.EnviarAsync(Arg.Do<clsMensajeCorreo>(m => _enviado = m), Arg.Any<CancellationToken>()).Returns("<abc@boterosoto.com.co>");
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

        resultado.Valor.Should().Be(new clsSalidaEnvioCorreo(true, "<abc@boterosoto.com.co>", true));
        _enviado!.Remitente.Should().Be("nvargas@boterosoto.com.co");
        _enviado.Para.Should().Equal("cliente@empresa.com");
        _enviado.Cc.Should().Equal("jefe@boterosoto.com.co");
        _enviado.Adjunto.Nombre.Should().Be("PCN-C1.pdf");
        _enviado.Adjunto.Contenido.Should().Equal("%PDF-1.4 prueba"u8.ToArray());
        _enviado.Asunto.Should().Be("Cotización PCN-C1 — Botero Soto");
        _enviado.CuerpoTexto.Should().Be(clsPlantillaCorreo.CuerpoPorDefecto);
        _enviado.CuerpoHtml.Should().Contain("Estimado cliente,<br>").And.Contain("Botero Soto</b> · Soluciones Logísticas");

        var fila = _registrados.Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoCorreo.Enviado);
        fila.MessageId.Should().Be("<abc@boterosoto.com.co>");
        fila.ConfirmadoPor.Should().Be(_comercial.Id);
        fila.Para.Should().Equal("cliente@empresa.com");
        fila.Cc.Should().Equal("jefe@boterosoto.com.co");
        _cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Enviada);
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
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

    private async Task NoSeEnvioNadaAsync()
    {
        await _transporte.DidNotReceiveWithAnyArgs().EnviarAsync(default!, Ct);
        _registrados.Should().BeEmpty();
    }
}
