using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Envío a costeo en Nexo (HU-005.5, ADR-015 §3): la cotización se confirma antes de llamar a Nexo; cualquier fallo la deja en
/// <c>error_envio</c> con el motivo (nunca se pierde) y se puede reintentar sin volver a llenar el formulario.
/// </summary>
public sealed class clsEnvioCosteoServiceTests
{
    private const string Nit = "900123456";

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsMargenResolverService _margen = Substitute.For<IClsMargenResolverService>();
    private readonly IClsGeneradorFolioService _folios = Substitute.For<IClsGeneradorFolioService>();
    private readonly IClsBuscadorClientes _buscador = Substitute.For<IClsBuscadorClientes>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsValidadorCatalogosCaptura _catalogos = Substitute.For<IClsValidadorCatalogosCaptura>();
    private readonly IClsValidadorFilasLicitacion _filas = FilasQuePasan();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsFijacionMonedaService _monedas = Substitute.For<IClsFijacionMonedaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsMargenPolitica _politica = clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: "PuertoAInterior").Valor;
    private readonly List<string> _eventos = [];
    private readonly clsOpcionesCosteoNexo _opciones = new();
    private clsCotizacionSolicitud? _creada;

    public clsEnvioCosteoServiceTests()
    {
        _margen.ResolverLineaAsync("Importación", Arg.Any<CancellationToken>()).Returns(_politica);
        _politicas.ObtenerPorIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_politica);
        _margen.ResolverAsync(Arg.Any<Cotifly.Aplicacion.Margen.DTOs.clsEntradaMargen>(), Arg.Any<CancellationToken>()).Returns(
            new Cotifly.Aplicacion.Margen.DTOs.clsMargenResuelto(
                new Cotifly.Aplicacion.Margen.DTOs.clsSalidaMargen(0.18m, "sap_tm", "ZIMP", false, "SAP TM"), "Importacion", 3, 0.15m, "margen"));
        _folios.CrearConFolioNuevoAsync(_ana, Nit, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var folio = clsFolio.Crear(new DateOnly(2026, 9, 28), "AP", 1, Nit).Valor;
                return ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(folio, CancellationToken.None);
            });
        _cotizaciones.When(r => r.Agregar(Arg.Any<clsCotizacionSolicitud>())).Do(ci => _creada = ci.Arg<clsCotizacionSolicitud>());
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _eventos.Add($"guardar:{(_creada?.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : "-")}");
            return 1;
        });
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _eventos.Add("nexo");
            return new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false);
        });
        ClienteEnCatalogo(new clsClienteEncontrado("900.123.456", "LONGYIN S.A.S.", "cliente", "15927"));
        _monedas.FijarAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new clsMonedaFijada(clsCotizacionSolicitud.MonedaPesos, 0m));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LaCotizacionSeConfirmaEnPendienteEnvioAntesDeLlamarANexoYLuegoQuedaPendienteDeCosteo()
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        _eventos.Should().Equal("guardar:pendiente_envio", "nexo", "guardar:pendiente_costeo");
        var cotizacion = resultado.Valor;
        cotizacion.Folio.Should().Be("PCN-900123456-AP-28092026-001", "el folio lo pone el servidor con el NIT (ADR-011)");
        cotizacion.UsuarioId.Should().Be(_ana);
        cotizacion.NitCliente.Should().Be(Nit);
        cotizacion.SolicitudNexoId.Should().Be("sol-1");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        cotizacion.CosteoEnviadoAt.Should().Be(_reloj.AhoraUtc);
        cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc.AddSeconds(5));
        cotizacion.PrecioFinal.Should().BeNull("el costo de Nexo todavía no pasó por el margen");

        // HU-021: la cabecera se guarda con su agregado: la ruta 1 con sus dos mercancías y un camión, también en pendiente_costeo.
        _creada.Should().BeSameAs(cotizacion);
        var ruta = cotizacion.Rutas.Should().ContainSingle().Subject;
        ruta.Mercancias.Should().HaveCount(2);
        ruta.Vehiculos.Should().ContainSingle().Which.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
    }

    [Fact]
    public async Task ElClienteSaleDelCatalogoDeNexoConSuBpYLaLineaVaConSuMapeo()
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { ClienteNombre = "longyin" }, Ct);

        resultado.Valor.ClienteNombre.Should().Be("LONGYIN S.A.S.");
        resultado.Valor.OrigenCliente.Should().Be(OrigenCliente.Cliente);
        await _nexo.Received(1).EnviarSolicitudAsync(_ana,
            Arg.Is<clsSolicitudNexo>(s => s.ClienteBp == "15927" && s.LineaNegocioNexo == "PuertoAInterior" && s.Solicitud.Origen == "Buenaventura"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElMargenSeResuelveAlEnviarConElBpDelCatalogoParaAplicarloCuandoLlegueElCosteo()
    {
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        // HU-021: el margen es de la ruta (de su línea de negocio).
        var ruta = cotizacion.PrimeraRuta!;
        ruta.MargenAplicado.Should().Be(0.18m);
        ruta.MargenPoliticaSugerido.Should().Be(0.15m);
        ruta.MargenSAPSugerido.Should().Be(0.18m);
        ruta.OrigenMargen.Should().Be(OrigenMargen.SapTm);
        ruta.MetodoCalculo.Should().Be("margen");
        cotizacion.PrecioFinal.Should().BeNull("sin costeo no hay precio");
        await _margen.Received(1).ResolverAsync(
            Arg.Is<Cotifly.Aplicacion.Margen.DTOs.clsEntradaMargen>(e => e.BpCliente == "15927" && e.LineaNegocio == "Importacion"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiElClienteSeDesconectaDuranteElAltaLaRespuestaDeNexoSeGuardaIgual()
    {
        using var peticion = new CancellationTokenSource();
        var tokensNexo = new List<CancellationToken>();
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            tokensNexo.Add(ci.Arg<CancellationToken>());
            peticion.Cancel(); // el comercial cierra la pestaña cuando Nexo ya recibió el POST
            return new clsSolicitudCreadaNexo("sol-9", 1, 0, 1, false);
        });

        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), peticion.Token)).Valor;

        tokensNexo.Should().ContainSingle().Which.CanBeCanceled.Should().BeFalse("el alta no se ata a la conexión del cliente");
        cotizacion.SolicitudNexoId.Should().Be("sol-9");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "no queda en pendiente_envio sin costeoId (un reintento duplicaría la solicitud)");
        await _unidad.Received().GuardarCambiosAsync(CancellationToken.None);
        await _buscador.Received(1).BuscarAsync(_ana, Nit, Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task ElReintentoVuelveAValidarLoGuardadoAntesDeReenviar()
    {
        var cotizacion = await CotizacionEnErrorAsync();

        // HU-021: las mercancías guardadas son de la ruta (se leen con el agregado); se simula una fila que ya no cuadra con el peso.
        var ruta = cotizacion.PrimeraRuta!;
        var mercancias = (List<clsMercancia>)typeof(clsLineaSolicitud)
            .GetField("_mercancias", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(ruta)!;
        mercancias.Clear();
        mercancias.AddRange(clsMercancia.DesdeCaptura(ruta.Id, [new clsPosicionCarga(null, 1, 10m, 10m, 10m, 1m)]));
        _nexo.ClearReceivedCalls();

        var resultado = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Detalle.Should().Be(clsSolicitudCosteo.PesoInconsistente);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task SiNexoYaCosteoLaLineaSeLeeElCostoEnLaMismaPeticionYQuedaEnAutocosteoConPrecio()
    {
        // HU-013.14 (opción A de ADR-018 §17): "autocosteo" nunca sale sin precio_final.
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-2", 1, 1, 0, false));
        _nexo.ConsultarCosteoAsync(_ana, "sol-2", Arg.Any<CancellationToken>()).Returns(FotoCosteada(2_050_000m));
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.PrecioFinal.Should().Be(2_500_000m, "2.050.000 / (1 - 0,18) con el margen de SAP TM");
        cotizacion.CotizacionNexoId.Should().Be("COT-9");
        cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("en autocosteo el polling sigue hasta la entrega");
        cotizacion.VersionNegocio.Should().Be(1, "el precio es un cambio de negocio");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(cotizacion.Id, 0, Arg.Any<CancellationToken>());
        _eventos.Should().Contain("guardar:pendiente_costeo", "el costeoId se guarda antes de leer el costo");
    }

    [Fact]
    public async Task SiLaLecturaInmediataFallaQuedaEnPendienteCosteoSinPrometerPrecio()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-2", 1, 1, 0, false));
        _nexo.ConsultarCosteoAsync(_ana, "sol-2", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "Nexo caído"));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "sin leer el costo no se dice autocosteo");
        resultado.Valor.PrecioFinal.Should().BeNull();
        resultado.Valor.CosteoSiguienteConsultaAt.Should().NotBeNull("el polling la completa");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task SiLaLecturaInmediataSuperaSuTiempoQuedaEnPendienteCosteoParaElPolling()
    {
        _opciones.LecturaInmediataSegundos = 1;
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-2", 1, 1, 0, false));
        _nexo.ConsultarCosteoAsync(_ana, "sol-2", Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            await Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
            return clsResultado<clsCosteoConsultadoNexo>.Exito(FotoCosteada(2_050_000m));
        });

        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        cronometro.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "la lectura tiene su propio tiempo de espera corto");
        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        resultado.Valor.PrecioFinal.Should().BeNull();
        resultado.Valor.CosteoSiguienteConsultaAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SinCosteoEnLaMismaPeticionNoSeConsultaANexoAntesDelPolling()
    {
        await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    [Theory]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado, "Nexo no respondió a tiempo", null)]
    [InlineData(clsCodigosError.CosteoNexoRechazada, "Nexo rechazó la solicitud.", "HTTP 400 VALIDATION_ERROR: El BP no existe")]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible, "El costeo de Nexo no está disponible", "HTTP 503 (ServiceUnavailable)")]
    public async Task UnFalloDeNexoDejaLaCotizacionEnErrorEnvioConElMotivoYNoSePierde(string codigo, string mensaje, string? detalle)
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, mensaje, detalle));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.EsExitoso.Should().BeTrue("el comercial no cometió un error: la cotización queda guardada para reintentar");
        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        resultado.Valor.MotivoEstadoCosteo.Should().StartWith(mensaje);
        if (detalle is not null)
        {
            resultado.Valor.MotivoEstadoCosteo.Should().Contain(detalle);
        }

        resultado.Valor.CosteoSiguienteConsultaAt.Should().BeNull("sin solicitud en Nexo no hay nada que consultar");
        _eventos.Should().Equal("guardar:pendiente_envio", "guardar:error_envio");
        await _nexo.Received(1).EnviarSolicitudAsync(_ana, Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(clsEnvioCosteoService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnRechazo400DeNexoEsDefinitivoYNoSeReintenta()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudInvalida, "Nexo rechazó los datos de la solicitud.", "HTTP 400 LINEA_INCOMPLETA"));
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;
        Registrada(cotizacion);

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        cotizacion.ErrorEnvioDefinitivo.Should().BeTrue();
        (await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoReintentable);
    }

    [Fact]
    public async Task UnFormularioQueNexoRechazariaPorPesoNoSeGuarda()
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { Solicitud = clsSolicitudCosteoMuestra.Crear() with { PesoKg = 10_000m } }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Detalle.Should().Be(clsSolicitudCosteo.PesoInconsistente);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task SiElCatalogoDeNexoNoRespondeQuedaEnErrorEnvioSinLlamarAlAlta()
    {
        _buscador.BuscarAsync(_ana, Nit, Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ClienteBusquedaNoDisponible, "caído"));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        resultado.Valor.MotivoEstadoCosteo.Should().Contain("catálogo de Nexo");
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task UnNitQueNoEstaEnElCatalogoOUnProspectoSinBpQuedanEnErrorEnvio()
    {
        ClienteEnCatalogo(new clsClienteEncontrado("800999999", "OTRO", "cliente", "1"));
        (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor.MotivoEstadoCosteo.Should().Contain("no está en el catálogo");

        ClienteEnCatalogo(new clsClienteEncontrado(Nit, "PROSPECTO S.A.S.", "prospecto"));
        var prospecto = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        prospecto.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        prospecto.MotivoEstadoCosteo.Should().Contain("prospecto").And.Contain("Pendiente de confirmar con el equipo de Nexo");
        prospecto.OrigenCliente.Should().Be(OrigenCliente.Prospecto);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task UnValorQueNoEsCodigoDeCatalogoNoSeGuardaNiLlegaANexo()
    {
        _catalogos.ValidarAsync(_ana, Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "El campo 'tipo_vehiculo' debe ser un código del catálogo"));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("tipo_vehiculo");
        _eventos.Should().BeEmpty();
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task SinCatalogoMaestroDisponibleNoSeGuardaNada()
    {
        _catalogos.ValidarAsync(_ana, Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "sin catálogo"));

        (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task ElServicioRevisaElMapeoDeLaLineaContraElCatalogoDeNexo()
    {
        await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        _catalogos.Received(1).RevisarMapeoLineaNexo(_politica);
    }

    [Fact]
    public async Task UnProspectoSinBpConIdDelCatalogoViajaPorClienteId()
    {
        var id = Guid.NewGuid();
        ClienteEnCatalogo(new clsClienteEncontrado(Nit, "PROSPECTO S.A.S.", "prospecto", null, id.ToString()));

        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        await _nexo.Received(1).EnviarSolicitudAsync(_ana, Arg.Is<clsSolicitudNexo>(s => s.Cliente.Id == id && s.Cliente.Bp == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConBpYClienteIdViajaElBp()
    {
        ClienteEnCatalogo(new clsClienteEncontrado(Nit, "LONGYIN S.A.S.", "cliente", " 15927 ", Guid.NewGuid().ToString()));

        await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        await _nexo.Received(1).EnviarSolicitudAsync(_ana, Arg.Is<clsSolicitudNexo>(s => s.Cliente.Bp == "15927" && s.Cliente.Id == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnRechazoDeCatalogoTraducidoPorNexoTambienEsDefinitivo()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "Nexo rechazó los datos", "HTTP 400 ENUM_INVALIDO: tipologia"));

        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        cotizacion.ErrorEnvioDefinitivo.Should().BeTrue();
        cotizacion.MotivoEstadoCosteo.Should().Contain("ENUM_INVALIDO");
    }

    [Fact]
    public void SoloLosRechazosDeDatosSonDefinitivos()
    {
        clsEnvioCosteoService.EsRechazoDefinitivo(new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudInvalida, "x")).Should().BeTrue();
        clsEnvioCosteoService.EsRechazoDefinitivo(new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "x")).Should().BeTrue();
        clsEnvioCosteoService.EsRechazoDefinitivo(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "x")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "nit_cliente")]
    [InlineData("  ", "nit_cliente")]
    [InlineData("123456789012345678901", "nit_cliente")]
    public async Task SinNitValidoNoSeGuardaNada(string? nit, string campo)
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { NitCliente = nit }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain(campo);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task UnFormularioInvalidoOUnaLineaDesconocidaNoGuardanNada()
    {
        var invalido = await Servicio().EnviarAsync(_ana, Entrada() with { Solicitud = clsSolicitudCosteoMuestra.Crear() with { Destino = "" } }, Ct);
        var linea = await Servicio().EnviarAsync(_ana, Entrada() with { LineaNegocio = "Marítimo" }, Ct);

        invalido.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        linea.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        _eventos.Should().BeEmpty();
        await _folios.DidNotReceiveWithAnyArgs().CrearConFolioNuevoAsync<clsCotizacionSolicitud>(default, default, default!, Ct);
    }

    [Fact]
    public async Task UnaLineaInactivaNoSePuedeEnviar()
    {
        var inactiva = clsMargenPolitica.Crear("Importacion", 0.15m).Valor;
        inactiva.Desactivar();
        _margen.ResolverLineaAsync("Importación", Arg.Any<CancellationToken>()).Returns(inactiva);

        (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Error.Detalle.Should().Be(clsMargenResolverService.DetalleLineaInactiva);
    }

    [Fact]
    public async Task ElReintentoReenviaLaSolicitudGuardadaSinVolverALlenarElFormulario()
    {
        var cotizacion = await CotizacionEnErrorAsync();
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, _reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(true);
        _nexo.ClearReceivedCalls();

        var resultado = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        resultado.Valor.SolicitudNexoId.Should().Be("sol-1");
        resultado.Valor.MotivoEstadoCosteo.Should().BeNull();
        // HU-021 (decisión 1): referencia y valor_estimado ya no viajan a Nexo; lo guardado (mercancías y ruta) sí.
        await _nexo.Received(1).EnviarSolicitudAsync(_ana,
            Arg.Is<clsSolicitudNexo>(s => s.Solicitud.Posiciones.Count == 2 && s.Solicitud.Origen == "Buenaventura" && s.Solicitud.PesoKg == 28_500.5m),
            Arg.Any<CancellationToken>());
        await _cotizaciones.Received(1).RecargarAsync(cotizacion, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiOtroReintentoGanoElReclamoNoSeVuelveAEnviar()
    {
        var cotizacion = await CotizacionEnErrorAsync();
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        _nexo.ClearReceivedCalls();

        var resultado = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNoReintentable);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task NoSeReintentaUnaCotizacionYaEnviadaNiUnaAjena()
    {
        var enviada = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;
        Registrada(enviada);

        (await Servicio().ReintentarAsync(_ana, enviada.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoReintentable);
        (await Servicio().ReintentarAsync(Guid.NewGuid(), enviada.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    [Fact]
    public async Task UnPendienteDeEnvioRecienteNoSeReintentaPorqueElEnvioOriginalPuedeSeguirEnCurso()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-P1", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), _politica.Id);
        Registrada(cotizacion);

        cotizacion.PuedeReenviarCosteo(cotizacion.UpdatedAt.AddMinutes(1)).Should().BeFalse();
        cotizacion.PuedeReenviarCosteo(cotizacion.UpdatedAt.AddMinutes(3)).Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public void ElMotivoSeRecortaAlLargoDeLaColumna()
    {
        var motivo = clsEnvioCosteoService.Motivo(new clsErrorDominio("X", new string('m', 900), new string('d', 900)));

        motivo.Length.Should().Be(clsCotizacionSolicitud.LargoMaximoMotivoCosteo);
    }

    [Fact]
    public async Task SinMonedaLaCotizacionQuedaEnPesosConTrmCero()
    {
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        cotizacion.Moneda.Should().Be(clsCotizacionSolicitud.MonedaPesos);
        cotizacion.TrmAplicada.Should().Be(0m);
        await _monedas.Received(1).FijarAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnDolaresLaTrmSeFijaAntesDeGuardarYQuedaEnLaCotizacion()
    {
        _monedas.FijarAsync("USD", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _eventos.Add("trm");
            return new clsMonedaFijada("USD", 4000m);
        });

        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada() with { Moneda = "USD" }, Ct)).Valor;

        _eventos.Should().Equal("trm", "guardar:pendiente_envio", "nexo", "guardar:pendiente_costeo");
        cotizacion.Moneda.Should().Be("USD");
        cotizacion.TrmAplicada.Should().Be(4000m, "la TRM del día de creación queda fija en la cotización (ADR-014 §5)");
    }

    [Fact]
    public async Task SinTrmNoSeGuardaNadaNiSeEnviaANexo()
    {
        _monedas.FijarAsync("USD", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "No se pudo obtener la TRM oficial del día."));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { Moneda = "USD" }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        _eventos.Should().BeEmpty();
        await _folios.DidNotReceiveWithAnyArgs().CrearConFolioNuevoAsync<clsCotizacionSolicitud>(default, default, default!, Ct);
        await _buscador.DidNotReceiveWithAnyArgs().BuscarAsync(default, default!, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("dólar")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OtraMonedaSeRechazaComoSolicitudInvalidaSinConsultarLaTrm(string moneda)
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { Moneda = moneda }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("'moneda'");
        _eventos.Should().BeEmpty();
        await _monedas.DidNotReceiveWithAnyArgs().FijarAsync(default, Ct);
    }

    [Fact]
    public async Task ElReintentoConservaLaMonedaYLaTrmFijadasAlCrear()
    {
        _monedas.FijarAsync("USD", Arg.Any<CancellationToken>()).Returns(new clsMonedaFijada("USD", 4000m));
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"), new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false));
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada() with { Moneda = "USD" }, Ct)).Valor;
        Registrada(cotizacion);
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, _reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(true);

        var resultado = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        resultado.Valor.Moneda.Should().Be("USD");
        resultado.Valor.TrmAplicada.Should().Be(4000m, "el reintento nunca vuelve a fijar la TRM");
        await _monedas.Received(1).FijarAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private async Task<clsCotizacionSolicitud> CotizacionEnErrorAsync()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"), new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false));
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        Registrada(cotizacion);
        return cotizacion;
    }

    private void Registrada(clsCotizacionSolicitud cotizacion)
    {
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
    }

    private void ClienteEnCatalogo(clsClienteEncontrado candidato) =>
        _buscador.BuscarAsync(_ana, Nit, Arg.Any<CancellationToken>()).Returns(new clsResultadoBusquedaClientes(true, [candidato]));

    private static clsEntradaSolicitudCosteo Entrada() => new(Nit, "Longyin", "Importación", clsSolicitudCosteoMuestra.Crear());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnMargenNegociadoSeConservaAlReenviarYElSugeridoSeRefrescaFalleONoLaResolucion(bool resolucionFalla)
    {
        // HU-013.4 (decisión del 2026-10-06): re-solicitar no pisa el margen ajustado por el comercial.
        var cotizacion = Negociada(0.22m);
        if (resolucionFalla)
        {
            _margen.ResolverAsync(Arg.Any<Cotifly.Aplicacion.Margen.DTOs.clsEntradaMargen>(), Arg.Any<CancellationToken>())
                .Returns(new clsErrorDominio(clsCodigosError.MargenFuenteNoDisponible, "SAP TM caído"));
        }

        await Servicio().EnviarGuardadaAsync(cotizacion, _politica, _ana, "Re-solicitud de F; reemplaza la solicitud sol-1.");

        // HU-021: el margen negociado y su sugerido son de la ruta.
        var ruta = cotizacion.PrimeraRuta!;
        ruta.MargenAplicado.Should().Be(0.22m);
        ruta.OrigenMargen.Should().Be(OrigenMargen.Negociado);
        ruta.SugeridoVigente.Should().Be(resolucionFalla ? 0.15m : 0.18m, "el sugerido se refresca con SAP TM como referencia");
        ruta.OrigenMargenSugerido.Should().Be(resolucionFalla ? OrigenMargen.PoliticaLinea : OrigenMargen.SapTm);
        await _nexo.Received(1).EnviarSolicitudAsync(_ana,
            Arg.Is<clsSolicitudNexo>(s => s.Encabezado.Notas!.EndsWith("Re-solicitud de F; reemplaza la solicitud sol-1.", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LaNotaDeTrazabilidadSeAgregaALasNotasSinSobrepasarElLimite()
    {
        var encabezado = clsSolicitudCosteoMuestra.Crear().Encabezado;
        const string Nota = "Re-solicitud de F; reemplaza la solicitud sol-1.";

        clsEnvioCosteoService.ConNota(encabezado with { Notas = null }, Nota).Notas.Should().Be(Nota);
        clsEnvioCosteoService.ConNota(encabezado with { Notas = "Llamar antes" }, Nota).Notas.Should().Be("Llamar antes\n" + Nota);
        var larga = clsEnvioCosteoService.ConNota(encabezado with { Notas = new string('x', 2000) }, Nota).Notas!;
        larga.Length.Should().Be(clsSolicitudCosteo.LargoNotas);
        larga.Should().EndWith("…\n" + Nota, "se recortan las notas existentes y la de trazabilidad queda completa");
        clsEnvioCosteoService.ConNota(encabezado, null).Should().Be(encabezado);
    }

    /// <summary>Solicitud guardada (ruta 1 con la política al 15 %) cuyo margen ajustó el comercial a <paramref name="margen"/>.</summary>
    private clsCotizacionSolicitud Negociada(decimal margen)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900123456-AP-28092026-001",
            UsuarioId = _ana,
            NitCliente = Nit,
        }).Valor;
        var ruta = cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), _politica.Id);
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        ruta.AplicarMargenNegociado(margen).Should().BeTrue();
        return cotizacion;
    }

    /// <summary>Validador de filas de licitación que deja pasar las líneas tal cual (cada prueba lo cambia si lo necesita).</summary>
    private static IClsValidadorFilasLicitacion FilasQuePasan()
    {
        var filas = Substitute.For<IClsValidadorFilasLicitacion>();
        filas.RevalidarGuardadasAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<clsEncabezadoSolicitudCosteo>(),
                Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<IReadOnlyList<int?>>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsResultado<IReadOnlyList<clsLineaSolicitudCosteo>>.Exito(ci.ArgAt<IReadOnlyList<clsLineaSolicitudCosteo>>(3)));
        return filas;
    }

    private clsEnvioCosteoService Servicio() => new(
        _cotizaciones, _politicas, _margen, _folios, _buscador, _nexo, _catalogos, _filas, _monedas, _unidad, _auditoria, _reloj,
        Options.Create(_opciones), NullLogger<clsEnvioCosteoService>.Instance, new clsPrecioCosteoService());

    /// <summary>Foto de Nexo con la única línea costeada (borrador, como el auto-costeo de la Biblioteca).</summary>
    private static clsCosteoConsultadoNexo FotoCosteada(decimal flete) =>
        new("EnCosteo", ["L1"], new clsCotizacionNexo("COT-9", clsCotizacionNexo.EstadoBorrador, "SinDefinir", [new clsLineaCosteoNexo("L1", flete, null, null)], []));
}
