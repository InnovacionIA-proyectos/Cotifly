using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
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
    private readonly IClsMunicipiosProvider _municipios = Substitute.For<IClsMunicipiosProvider>();
    private readonly IClsCostosConexosNexo _costosConexos = Substitute.For<IClsCostosConexosNexo>();
    private readonly IClsValidadorFilasLicitacion _filas;
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
        // HU-021.6: el validador real de las rutas, con catálogos que dejan pasar y el catálogo de municipios del Nexo falso.
        _filas = new clsValidadorFilasLicitacion(_catalogos, _municipios);
        _catalogos.ValidarLineasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsValidacionCatalogoLineas([.. ci.ArgAt<IReadOnlyList<clsLineaSolicitudCosteo>>(1).Select(_ => (IReadOnlyList<clsErrorCampo>)[])], null));
        _municipios.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(
            [
                new clsMunicipio(76109, "76109", "Buenaventura", "Valle del Cauca", "Colombia"), new clsMunicipio(11001, "11001", "Bogotá, D.C.", "Bogotá, D.C.", "Colombia"),
                new clsMunicipio(5001, "05001", "Medellín", "Antioquia", "Colombia"), new clsMunicipio(76001, "76001", "Cali", "Valle del Cauca", "Colombia"),
                new clsMunicipio(5400, "05400", "La Unión", "Antioquia", "Colombia"), new clsMunicipio(52399, "52399", "La Unión", "Nariño", "Colombia"),
            ]));
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
    public async Task HU0181_ElScorePrioridadQueDevuelveNexoSeGuardaEnLaCabeceraConElIdDeLaSolicitud()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false) { ScorePrioridad = 62.5m });

        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        cotizacion.SolicitudNexoId.Should().Be("sol-1");
        cotizacion.ScorePrioridad.Should().Be(62.5m, "antes Cotifly descartaba el scorePrioridad del CapturaResultDto");
    }

    [Fact]
    public async Task HU0181_SiNexoNoMandaScorePrioridadQuedaNull()
    {
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        cotizacion.ScorePrioridad.Should().BeNull();
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
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { Solicitud = clsSolicitudCosteoMuestra.CrearCaptura() with { PesoKg = 10_000m } }, Ct);

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
        _catalogos.ValidarLineasAsync(_ana, Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(new clsValidacionCatalogoLineas(
                [[new clsErrorCampo("clase_viaje", clsCodigosLineaSolicitud.CodigoNoCatalogo, "El campo 'clase_viaje' debe ser un código del catálogo")]], null));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("clase_viaje");
        _eventos.Should().BeEmpty();
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task SinCatalogoMaestroDisponibleNoSeGuardaNada()
    {
        _catalogos.ValidarLineasAsync(_ana, Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(new clsValidacionCatalogoLineas([[]], new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "sin catálogo")));

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
        var invalido = await Servicio().EnviarAsync(_ana, Entrada() with { Solicitud = clsSolicitudCosteoMuestra.CrearCaptura() with { Destino = "", DestinoMunicipioId = null } }, Ct);
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

    private static clsEntradaSolicitudCosteo Entrada() => new(Nit, "Longyin", "Importación", clsSolicitudCosteoMuestra.CrearCaptura());

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
    public async Task CadaLineaViajaConLaLineaDeNegocioDeSuRutaYUnaPorCamion()
    {
        // #134: la línea de negocio de Nexo viaja por línea, la de SU ruta; antes viajaba la de la ruta 1 en todas.
        var nacional = clsMargenPolitica.Crear("Nacional", 0.2m, mapeoLineasNexo: "Nacional").Valor;
        _politicas.ObtenerPorIdAsync(7, Arg.Any<CancellationToken>()).Returns(nacional);
        var cotizacion = Negociada(0.22m);
        cotizacion.PrimeraRuta!.AgregarVehiculo("2S2", null, 8_000m);
        cotizacion.AgregarRuta(new clsLineaSolicitudCosteo { Origen = "Cali", Destino = "Yumbo", PesoKg = 5_000m, TipoVehiculo = "2" }, 1, 7);
        clsSolicitudNexo? enviada = null;
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Do<clsSolicitudNexo>(s => enviada = s), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-1", 3, 0, 3, false));

        await Servicio().EnviarGuardadaAsync(cotizacion, _politica, _ana);

        enviada!.Lineas.Should().HaveCount(3, "una línea por camión");
        enviada.Lineas.Select(l => (l.Origen, l.LineaNegocioNexo)).Should().Equal(
            ("Buenaventura", "PuertoAInterior"), ("Buenaventura", "PuertoAInterior"), ("Cali", "Nacional"));
        enviada.Encabezado.Notas.Should().Contain("Ruta 1 (Buenaventura → Bogotá D.C.): 2 camiones. Ruta 2 (Cali → Yumbo): 1 camión.");
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

    // ------------------------------------------------------------------ HU-021.6: captura por rutas

    [Fact]
    public async Task CadaRutaSeGuardaConSuLineaDeNegocioYUnCamionSinVehiculoConElPesoTotal()
    {
        var exportacion = ConId(clsMargenPolitica.Crear("Exportacion", 0.12m).Valor, 7);
        ConId(_politica, 3);
        _margen.ResolverLineaAsync("Exportación", Arg.Any<CancellationToken>()).Returns(exportacion);
        var segunda = Ruta(5001, "Medellín", 76109, "Buenaventura", "Exportación") with
        {
            Linea = Ruta(5001, "Medellín", 76109, "Buenaventura").Linea with { PesoKg = null },
        };

        var cotizacion = (await Servicio().CapturarAsync(_ana, Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."), segunda), Ct)).Valor;

        var rutas = cotizacion.RutasOrdenadas;
        rutas.Should().HaveCount(2);
        rutas[0].LineaNegocioId.Should().Be(3, "sin línea propia, la ruta usa la de la solicitud");
        rutas[1].LineaNegocioId.Should().Be(7);
        rutas[1].RutaOrigenIndice.Should().Be(1, "en el formulario el índice de origen es la posición de la ruta");
        var camion = rutas[0].Vehiculos.Should().ContainSingle().Subject;
        camion.TipoVehiculo.Should().BeNull("el vehículo lo define la Mesa de Costeo de Nexo hasta que exista el cubicador (decisión 7)");
        camion.Carroceria.Should().BeNull();
        camion.PesoKg.Should().Be(28_500.5m);
        rutas[1].Vehiculos.Should().ContainSingle().Which.PesoKg.Should().Be(28_500m, "sin peso declarado, el camión lleva la suma de las mercancías");
        await _nexo.Received(1).EnviarSolicitudAsync(_ana, Arg.Is<clsSolicitudNexo>(s => s.Lineas.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElVehiculoYLaCarroceriaQueLleganSeIgnoran()
    {
        var conVehiculo = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.");
        conVehiculo = conVehiculo with { Linea = conVehiculo.Linea with { TipoVehiculo = "3S3", Carroceria = "FURGON" } };

        var cotizacion = (await Servicio().CapturarAsync(_ana, Captura(conVehiculo), Ct)).Valor;

        var camion = cotizacion.PrimeraRuta!.Vehiculos.Should().ContainSingle().Subject;
        camion.TipoVehiculo.Should().BeNull();
        camion.Carroceria.Should().BeNull();
        await _catalogos.Received().ValidarLineasAsync(_ana,
            Arg.Is<IReadOnlyList<clsLineaSolicitudCosteo>>(l => l.All(x => x.TipoVehiculo == null && x.Carroceria == null)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConSoloElIdElNombreDelMunicipioSeCopiaDelCatalogoYLasCiudadesDelContenedorSeResuelven()
    {
        var ruta = Ruta(76109, null, 11001, null);
        ruta = ruta with
        {
            Linea = ruta.Linea with
            {
                CiudadDevolucionContenedor = "Buenaventura", CiudadDevolucionMunicipioId = null, CiudadLevanteMunicipioId = 76001, LevanteContenedorDiferente = true,
            },
        };

        var guardada = (await Servicio().CapturarAsync(_ana, Captura(ruta), Ct)).Valor.PrimeraRuta!;

        guardada.OrigenMunicipioId.Should().Be(76109);
        guardada.OrigenNombre.Should().Be("Buenaventura");
        guardada.DestinoNombre.Should().Be("Bogotá, D.C.");
        guardada.CiudadDevolucionMunicipioId.Should().Be(76109, "la ciudad por nombre se resuelve con el catálogo");
        guardada.CiudadDevolucionNombre.Should().Be("Buenaventura");
        guardada.CiudadLevanteMunicipioId.Should().Be(76001);
        guardada.CiudadLevanteNombre.Should().Be("Cali", "con solo el id el nombre se copia del catálogo");
    }

    [Fact]
    public async Task SinCubicadorConfiguradoUnaMercanciaSinCodigoSeGuardaSinCodigoYLaRutaVaAlRespaldo()
    {
        // HU-023.3 (auditor): sin cubicador GET /web/tipos-mercancia responde 503 y no hay de dónde sacar el código.
        var ruta = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.");
        ruta = ruta with { Linea = ruta.Linea with { Posiciones = [new clsPosicionCarga("Cajas", 1, 10m, 10m, 10m, 28_500.5m, Guid.NewGuid())] } };

        var guardada = (await Servicio().CapturarAsync(_ana, Captura(ruta), Ct)).Valor.PrimeraRuta!;

        guardada.MercanciasOrdenadas.Should().ContainSingle().Which.CodigoMercancia.Should().BeNull();
        guardada.Vehiculos.Should().ContainSingle().Which.TipoVehiculo.Should().BeNull();
        guardada.ResultadoCubicacion.Should().BeNull();
    }

    [Fact]
    public async Task TodosLosErroresDeLasRutasLleganJuntosYNoSeGuardaNada()
    {
        var sinTipo = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.");
        sinTipo = sinTipo with { Linea = sinTipo.Linea with { Posiciones = [new clsPosicionCarga("Cajas", 1, 10m, 10m, 10m, 28_500.5m)] } };
        var ambigua = Ruta(null, "La Unión", 11001, "Bogotá D.C.");
        var sinLinea = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.");
        sinLinea = sinLinea with { Linea = sinLinea.Linea with { PesoKg = null, Posiciones = [] } };

        // HU-023.3: con el cubicador configurado el codigo_mercancia es obligatorio.
        var resultado = await ServicioCon(Cubicador()).CapturarAsync(_ana, Captura(sinTipo, ambigua, sinLinea), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().StartWith("Ruta 1:").And.Contain("detalle.errores");
        var errores = ((IEnumerable<IReadOnlyDictionary<string, object?>>)resultado.Error.Datos!["errores"]!).ToList();
        errores.Select(e => ((int?)e["ruta"], (string)e["campo"]!, (string)e["codigo"]!)).Should().BeEquivalentTo(
        [
            ((int?)0, "mercancias[0].codigo_mercancia", clsCodigosLineaSolicitud.CampoRequerido),
            ((int?)1, "origen", clsCodigosLineaSolicitud.MunicipioAmbiguo),
            ((int?)2, "peso_kg", clsCodigosLineaSolicitud.PesoRequerido),
        ]);
        ((string)errores[1]["mensaje"]!).Should().Contain("id 5400").And.Contain("id 52399", "nunca adivina: devuelve los candidatos");
        _eventos.Should().BeEmpty();
        await _folios.DidNotReceiveWithAnyArgs().CrearConFolioNuevoAsync<clsCotizacionSolicitud>(default, default, default!, Ct);
    }

    [Fact]
    public async Task SinCatalogoDeMunicipiosNoSeGuardaNada()
    {
        _municipios.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, "caído"));

        var resultado = await Servicio().CapturarAsync(_ana, Captura(Ruta(null, "Buenaventura", 11001, "Bogotá D.C.")), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task LosConexosSeGuardanConLaRutaConElCostoDelCatalogoDeNexoComoPrecio()
    {
        _costosConexos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(
                new Dictionary<string, clsCostoConexoNexo> { ["SC-1"] = new("SC-1", "Cargue", 150_000m) }));
        var ruta = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.") with { Conexos = [new clsConexoCapturaEntrada(" SC-1 ", 2m)] };

        var guardada = (await Servicio().CapturarAsync(_ana, Captura(ruta), Ct)).Valor.PrimeraRuta!;

        var conexo = guardada.Conexos.Should().ContainSingle().Subject;
        conexo.IdServicioConexo.Should().Be("SC-1");
        conexo.Nombre.Should().Be("Cargue");
        conexo.Precio.Should().Be(150_000m, "el precio del conexo es el costo de Nexo (decisión 3)");
        conexo.Cantidad.Should().Be(2m);
        guardada.CostoServiciosConexos.Should().Be(300_000m);
        guardada.PrecioFinalLinea.Should().BeNull("sin flete no hay precio");
    }

    [Fact]
    public async Task UnConexoQueNoEstaEnElCatalogoOMalFormadoEsUnErrorDeSuRuta()
    {
        _costosConexos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(new Dictionary<string, clsCostoConexoNexo>()));
        var desconocido = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.") with { Conexos = [new clsConexoCapturaEntrada("SC-9", 1m)] };
        var repetido = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.") with
        {
            Conexos = [new clsConexoCapturaEntrada("SC-1", 1m), new clsConexoCapturaEntrada("sc-1", 0m)],
        };

        var noEncontrado = await Servicio().CapturarAsync(_ana, Captura(desconocido), Ct);
        var malFormado = await Servicio().CapturarAsync(_ana, Captura(repetido), Ct);

        noEncontrado.Error.Detalle.Should().Be(clsCodigosLineaSolicitud.ConexoNoEncontrado);
        CodigosDe(malFormado.Error).Should().Equal(clsCodigosLineaSolicitud.ConexoDuplicado, clsCodigosLineaSolicitud.ValorFueraDeRango);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task SiNexoNoRespondeLosCostosDeLosConexosNoSeGuardaNada()
    {
        _costosConexos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "caído"));
        var ruta = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.") with { Conexos = [new clsConexoCapturaEntrada("SC-1", 1m)] };

        (await Servicio().CapturarAsync(_ana, Captura(ruta), Ct)).Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task SiElPlanificadorDeCamionesFallaNoSeGuardaNada()
    {
        var planificador = Substitute.For<IClsPlanificadorCamiones>();
        planificador.PlanificarAsync(_ana, Arg.Any<Guid>(), Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "no se pudo calcular los vehículos"));
        var servicio = new clsEnvioCosteoService(
            _cotizaciones, _politicas, _margen, _folios, _buscador, _nexo, _catalogos, _filas, _monedas, _unidad, _auditoria, _reloj,
            Options.Create(_opciones), NullLogger<clsEnvioCosteoService>.Instance, new clsPrecioCosteoService(),
            new clsCapturaRutasService(_filas, _costosConexos, planificador, NullLogger<clsCapturaRutasService>.Instance));

        (await servicio.CapturarAsync(_ana, Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.")), Ct)).Error.Codigo
            .Should().Be(clsCodigosError.CubicadorNoDisponible);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElCubicadorLaRutaTieneUnCamionPorRecomendacionYLaMercanciaElNombreDelCatalogo()
    {
        // HU-021.5 (#133) y HU-023.1 (#156): la ruta viaja al cubicador con el id que tendrá al guardarse, cada mercancía con su código
        // (traducido del id_tipo_mercancia con el catálogo) y cada vehículo de la recomendación es un camión (flota mixta).
        var cubicador = Cubicador();
        clsPeticionRecomendacion? enviada = null;
        cubicador.RecomendarAsync(Arg.Do<clsPeticionRecomendacion>(p => enviada = p), Arg.Any<CancellationToken>())
            .Returns(new clsRecomendacionCubicador(Guid.NewGuid(), clsResultadosRecomendacion.Asignado, null, false, [],
            [
                new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, 60m, 90m, 50m),
                new clsVehiculoRecomendado(Guid.NewGuid(), "C2", "ESTACAS", 12_500m, 20m, 70m, 95m),
            ], [], null));

        var guardada = (await ServicioCon(cubicador).CapturarAsync(_ana, Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.")), Ct)).Valor.PrimeraRuta!;

        guardada.Id.ToString("D").Should().Be(enviada!.IdLineaSolicitud, "IdLineaSolicitud del cubicador = el id de la ruta");
        enviada.Mercancias.Should().OnlyContain(m => m.CodigoMercancia == clsSolicitudCosteoMuestra.CodigoMercancia);
        guardada.VehiculosOrdenados.Select(v => (v.TipoVehiculo, v.Carroceria, v.PesoKg))
            .Should().Equal(("3S3", "FURGON", 16_000m), ("C2", "ESTACAS", 12_500m));
        guardada.VehiculosOrdenados.Should().OnlyContain(v => v.IdRecomendacionVehiculo != null, "C3: se guarda el id exacto de la recomendación");
        guardada.MercanciasOrdenadas.Should().NotBeEmpty().And.OnlyContain(m => m.NombreMercancia == "Café en estibas");
    }

    [Fact]
    public async Task UnTipoDeMercanciaQueNoEstaEnElCatalogoDelCubicadorSeRechazaYSinCatalogoSeConservaElNombre()
    {
        var conCatalogo = Cubicador();
        conCatalogo.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito([new clsTipoMercanciaCubicador("OTRA", "Otra", null, true, false, null, true, Guid.NewGuid())]));
        var caido = Cubicador();
        caido.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "caído"));
        var ruta = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.");

        var rechazada = await ServicioCon(conCatalogo).CapturarAsync(_ana, Captura(ruta), Ct);
        var guardada = (await ServicioCon(caido).CapturarAsync(_ana, Captura(ruta), Ct)).Valor.PrimeraRuta!;

        CodigosDe(rechazada.Error).Should().OnlyContain(c => c == clsCodigosLineaSolicitud.MercanciaDesconocida).And.NotBeEmpty();
        guardada.MercanciasOrdenadas.Select(m => m.NombreMercancia)
            .Should().Equal(ruta.Linea.Posiciones.Select(p => p.Descripcion?.Trim() is { Length: > 0 } d ? d : null));
    }

    [Fact]
    public async Task UnaSolicitudSinRutasOSinLineaDeNegocioSeRechaza()
    {
        var sinRutas = await Servicio().CapturarAsync(_ana, Captura(), Ct);
        var sinLinea = await Servicio().CapturarAsync(_ana, Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.")) with { LineaNegocio = null }, Ct);

        CodigosDe(sinRutas.Error).Should().Equal(clsCodigosLineaSolicitud.LineasRequeridas);
        sinLinea.Error.Mensaje.Should().Contain("linea_negocio");
    }

    [Fact]
    public async Task ElNombreDelMunicipioSiempreSaleDelCatalogoYUnIdInexistenteSeRechaza()
    {
        var guardada = (await Servicio().CapturarAsync(_ana, Captura(Ruta(11001, "Medellín", 76109, "Cali")), Ct)).Valor.PrimeraRuta!;
        var inexistente = await Servicio().CapturarAsync(_ana, Captura(Ruta(99999, "Bogotá", 11001, "Bogotá")), Ct);

        (guardada.OrigenNombre, guardada.DestinoNombre).Should().Be(("Bogotá, D.C.", "Buenaventura"), "un nombre distinto del cliente se ignora (decisión 2)");
        CodigosDe(inexistente.Error).Should().Equal(clsCodigosLineaSolicitud.MunicipioInexistente);
        ((IEnumerable<IReadOnlyDictionary<string, object?>>)inexistente.Error.Datos!["errores"]!).Single()["campo"].Should().Be("origen_municipio_id");
    }

    [Fact]
    public async Task UnConexoQueNoEstaEnElCatalogoLlegaJuntoConLosDemasErrores()
    {
        _costosConexos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(new Dictionary<string, clsCostoConexoNexo>()));
        var ruta = Ruta(null, "La Unión", 11001, "Bogotá D.C.") with { Conexos = [new clsConexoCapturaEntrada("SC-9", 1m)] };

        var resultado = await Servicio().CapturarAsync(_ana, Captura(ruta), Ct);

        CodigosDe(resultado.Error).Should().BeEquivalentTo([clsCodigosLineaSolicitud.MunicipioAmbiguo, clsCodigosLineaSolicitud.ConexoNoEncontrado]);
    }

    [Fact]
    public async Task ElReintentoNoVuelveACotizarLosConexos()
    {
        _costosConexos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(
                new Dictionary<string, clsCostoConexoNexo> { ["SC-1"] = new("SC-1", "Cargue", 150_000m) }));
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"), new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false));
        var ruta = Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.") with { Conexos = [new clsConexoCapturaEntrada("SC-1", 2m)] };
        var cotizacion = (await Servicio().CapturarAsync(_ana, Captura(ruta), Ct)).Valor;
        Registrada(cotizacion);
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, _reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(true);
        _costosConexos.ClearReceivedCalls();

        var reintento = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        reintento.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        reintento.Valor.PrimeraRuta!.Conexos.Should().ContainSingle().Which.Precio.Should().Be(150_000m, "el precio es el costo de Nexo al capturar");
        await _costosConexos.DidNotReceiveWithAnyArgs().CostosAsync(default, default!, Ct);
    }

    [Fact]
    public async Task ElReintentoDeUnFormularioMigradoSinIdsLosResuelvePorElNombre()
    {
        // HU-021.6: una cotización anterior a la decisión 2 (sin ids de municipio) no viaja a Nexo sin ellos.
        var cotizacion = clsCotizacionMuestra.Formulario(usuarioId: _ana);
        cotizacion.RegistrarErrorEnvioCosteo("Nexo caído");
        Registrada(cotizacion);
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, _reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(true);

        var reintento = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        reintento.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        (cotizacion.PrimeraRuta!.OrigenMunicipioId, cotizacion.PrimeraRuta.DestinoMunicipioId).Should().Be(((int?)76109, (int?)11001));
        await _nexo.Received(1).EnviarSolicitudAsync(_ana,
            Arg.Is<clsSolicitudNexo>(s => s.Lineas[0].OrigenMunicipioId == 76109 && s.Lineas[0].DestinoMunicipioId == 11001), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElReintentoDeUnFormularioMigradoConUnMunicipioAmbiguoNoViajaANexo()
    {
        var cotizacion = clsCotizacionMuestra.Formulario(usuarioId: _ana, solicitud: clsSolicitudCosteoMuestra.Crear() with { Origen = "La Unión" });
        cotizacion.RegistrarErrorEnvioCosteo("Nexo caído");
        Registrada(cotizacion);

        var reintento = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        reintento.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        CodigosDe(reintento.Error).Should().Equal(clsCodigosLineaSolicitud.MunicipioAmbiguo);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    private static clsEntradaCaptura Captura(params clsLineaLicitacionEntrada[] rutas) =>
        new(Nit, "Longyin", "Importación", clsSolicitudCosteoMuestra.CrearCaptura().Encabezado, rutas);

    /// <summary>Ruta de la muestra de captura con otro origen y destino (id y nombre; <c>null</c> = no viene).</summary>
    private static clsLineaLicitacionEntrada Ruta(int? origenId, string? origen, int? destinoId, string? destino, string? linea = null) =>
        new(null, clsSolicitudCosteoMuestra.RutaCaptura() with
        {
            OrigenMunicipioId = origenId,
            Origen = origen ?? string.Empty,
            DestinoMunicipioId = destinoId,
            Destino = destino ?? string.Empty,
        }, linea);

    private static IEnumerable<string> CodigosDe(clsErrorDominio error) =>
        ((IEnumerable<IReadOnlyDictionary<string, object?>>)error.Datos!["errores"]!).Select(e => (string)e["codigo"]!);

    private static clsMargenPolitica ConId(clsMargenPolitica politica, int id)
    {
        typeof(clsMargenPolitica).GetProperty(nameof(clsMargenPolitica.Id))!.SetValue(politica, id);
        return politica;
    }

    /// <summary>Cubicador configurado con el catálogo de la muestra y una recomendación de un camión.</summary>
    private static IClsCubicador Cubicador()
    {
        var cubicador = Substitute.For<IClsCubicador>();
        cubicador.EstaConfigurado.Returns(true);
        cubicador.RespaldoSiFalla.Returns(true);
        cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito([new clsTipoMercanciaCubicador(
                clsSolicitudCosteoMuestra.CodigoMercancia, "Café en estibas", null, true, false, null, true, clsSolicitudCosteoMuestra.TipoMercancia)]));
        cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>())
            .Returns(new clsRecomendacionCubicador(Guid.NewGuid(), clsResultadosRecomendacion.Asignado, null, false, [],
                [new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 28_500m, 60m, 90m, 80m)], [], null));
        return cubicador;
    }

    private clsEnvioCosteoService ServicioCon(IClsCubicador cubicador) => new(
        _cotizaciones, _politicas, _margen, _folios, _buscador, _nexo, _catalogos, _filas, _monedas, _unidad, _auditoria, _reloj,
        Options.Create(_opciones), NullLogger<clsEnvioCosteoService>.Instance, new clsPrecioCosteoService(),
        new clsCapturaRutasService(_filas, _costosConexos,
            new clsPlanificadorCamionesCubicador(NullLogger<clsPlanificadorCamionesCubicador>.Instance, cubicador),
            NullLogger<clsCapturaRutasService>.Instance, cubicador));

    /// <summary>
    /// #173 (observación del validador): Nexo recibe la solicitud y el guardado posterior falla. El almacén de idempotencia no guarda un
    /// resultado (la petición lanzó), pero la clave quedó registrada con el folio ANTES de llamar a Nexo y con el solicitudNexoId después:
    /// el reintento con la misma clave NO vuelve a llamar a Nexo y responde ESTADO_NO_PERMITE (CAPTURA_EN_CURSO) con el folio.
    /// </summary>
    [Fact]
    public async Task SiFallaElGuardadoDespuesDeNexoElReintentoConLaMismaClaveNoVuelveALlamarANexo()
    {
        var guardados = 0;
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ =>
            ++guardados == 2 ? throw new TimeoutException("Execution Timeout Expired") : Task.FromResult(1));
        var registro = new clsRegistroCapturasIdempotentesEnMemoria(_reloj);
        var servicio = ServicioIdempotente(registro);
        var entrada = Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."));

        var primera = () => servicio.CapturarUnaVezAsync(_ana, entrada, "clave-173-ab", Ct);
        await primera.Should().ThrowAsync<TimeoutException>();
        var folio = _creada!.Folio;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, _ana, Arg.Any<CancellationToken>()).Returns((clsCotizacionSolicitud?)null);

        var reintento = await servicio.CapturarUnaVezAsync(_ana, entrada, "clave-173-ab", Ct);

        await _nexo.Received(1).EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
        reintento.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        reintento.Error.Detalle.Should().Be(clsEnvioCosteoService.MotivoCapturaEnCurso);
        reintento.Error.Datos!["folio"].Should().Be(folio);
        reintento.Error.Datos!["solicitud_nexo_id"].Should().Be("sol-1", "el id de Nexo quedó registrado aunque el guardado falló");
    }

    /// <summary>
    /// #173 (segunda ronda del validador): timeout en el guardado posterior a Nexo y después "reintentar" desde la consola. El folio quedó
    /// en pendiente_envio sin solicitudNexoId; el registro de la clave sí conoce el id de Nexo: se vincula (pendiente_costeo) y NO se reenvía.
    /// </summary>
    [Fact]
    public async Task ElReintentoDeUnFolioCuyoGuardadoFalloTrasNexoLoVinculaSinReenviar()
    {
        var guardados = 0;
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ =>
            ++guardados == 2 ? throw new TimeoutException("Execution Timeout Expired") : Task.FromResult(1));
        var registro = new clsRegistroCapturasIdempotentesEnMemoria(_reloj);
        var servicio = ServicioIdempotente(registro);
        var entrada = Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."));
        var primera = () => servicio.CapturarUnaVezAsync(_ana, entrada, "clave-173-ef", Ct);
        await primera.Should().ThrowAsync<TimeoutException>();

        // Lo que quedó en la base: el mismo folio en pendiente_envio y sin solicitudNexoId (la captura se confirmó; el envío no se guardó).
        _buscador.BuscarAsync(_ana, Nit, Arg.Any<CancellationToken>()).Returns<Task<clsResultado<clsResultadoBusquedaClientes>>>(_ => throw new InvalidOperationException("corte"));
        try
        {
            await Servicio().CapturarAsync(_ana, entrada, Ct);
        }
        catch (InvalidOperationException)
        {
            // El corte antes de Nexo deja el folio confirmado y sin enviar.
        }

        var guardada = _creada!;
        guardada.EstadoCosteo.Should().BeOneOf(EstadoCosteo.PendienteEnvio, EstadoCosteo.ErrorEnvio);
        guardada.SolicitudNexoId.Should().BeNull();
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(guardada.Folio, _ana, Arg.Any<CancellationToken>()).Returns(guardada);
        _reloj.Avanzar(clsCotizacionSolicitud.EsperaReenvioPendiente + TimeSpan.FromMinutes(1));

        var reintento = await servicio.ReintentarAsync(_ana, guardada.Folio, Ct);

        reintento.Valor.SolicitudNexoId.Should().Be("sol-1", "se vincula la solicitud que Nexo ya tenía");
        reintento.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "el polling la sigue normalmente");
        reintento.Valor.CosteoSiguienteConsultaAt.Should().NotBeNull();
        await _nexo.Received(1).EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// HU-018.1 (#155) + #173: el reintento que vincula por el registro de la Idempotency-Key guarda también el scorePrioridad que Nexo
    /// devolvió con el id (antes quedaba null porque el registro no lo tenía).
    /// </summary>
    [Fact]
    public async Task ElReintentoQueVinculaPorElRegistroGuardaElScorePrioridadDeNexo()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false) { ScorePrioridad = 62.5m });
        var guardados = 0;
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ =>
            ++guardados == 2 ? throw new TimeoutException("Execution Timeout Expired") : Task.FromResult(1));
        var registro = new clsRegistroCapturasIdempotentesEnMemoria(_reloj);
        var servicio = ServicioIdempotente(registro);
        var entrada = Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."));
        var primera = () => servicio.CapturarUnaVezAsync(_ana, entrada, "clave-155-sc", Ct);
        await primera.Should().ThrowAsync<TimeoutException>();
        (await registro.BuscarPorFolioAsync(_creada!.Folio, Ct))!.ScorePrioridad.Should().Be(62.5m, "el registro guarda el score con el id de Nexo");

        var guardada = await FolioPendienteSinIdDeNexoAsync(entrada);

        var reintento = await servicio.ReintentarAsync(_ana, guardada.Folio, Ct);

        reintento.Valor.SolicitudNexoId.Should().Be("sol-1");
        reintento.Valor.ScorePrioridad.Should().Be(62.5m, "la vinculación guarda el mismo score que el primer envío");
        await _nexo.Received(1).EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Un registro sin score (Nexo no lo mandó, o la captura se registró antes de guardar el score en el registro): la vinculación sigue y
    /// el score queda null, sin romper el flujo ni llamar a Nexo.
    /// </summary>
    [Fact]
    public async Task ElReintentoQueVinculaConUnRegistroSinScoreLoDejaNullYNoReenvia()
    {
        var registro = new clsRegistroCapturasIdempotentesEnMemoria(_reloj);
        var servicio = ServicioIdempotente(registro);
        var entrada = Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."));
        var guardada = await FolioPendienteSinIdDeNexoAsync(entrada);
        await registro.RegistrarAsync($"{clsEnvioCosteoService.OperacionIdempotencia}:{_ana}", "clave-anterior",
            new clsCapturaIdempotente("huella", guardada.Id, guardada.Folio, "sol-previa", _reloj.AhoraUtc), Ct);

        var reintento = await servicio.ReintentarAsync(_ana, guardada.Folio, Ct);

        reintento.Valor.SolicitudNexoId.Should().Be("sol-previa");
        reintento.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        reintento.Valor.ScorePrioridad.Should().BeNull();
        await _nexo.DidNotReceive().EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Deja el folio como lo deja el guardado fallido tras Nexo: confirmado, en pendiente_envio y sin solicitudNexoId (un corte antes de
    /// Nexo), con el reloj pasada la espera de reenvío.
    /// </summary>
    private async Task<clsCotizacionSolicitud> FolioPendienteSinIdDeNexoAsync(clsEntradaCaptura entrada)
    {
        _buscador.BuscarAsync(_ana, Nit, Arg.Any<CancellationToken>()).Returns<Task<clsResultado<clsResultadoBusquedaClientes>>>(_ => throw new InvalidOperationException("corte"));
        try
        {
            await Servicio().CapturarAsync(_ana, entrada, Ct);
        }
        catch (InvalidOperationException)
        {
            // El corte antes de Nexo deja el folio confirmado y sin enviar.
        }

        var guardada = _creada!;
        guardada.SolicitudNexoId.Should().BeNull();
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(guardada.Folio, _ana, Arg.Any<CancellationToken>()).Returns(guardada);
        _reloj.Avanzar(clsCotizacionSolicitud.EsperaReenvioPendiente + TimeSpan.FromMinutes(1));
        return guardada;
    }

    [Fact]
    public async Task ConLaCapturaYaGuardadaElReintentoConLaMismaClaveDevuelveElFolioSinLlamarANexo()
    {
        var servicio = ServicioIdempotente(new clsRegistroCapturasIdempotentesEnMemoria(_reloj));
        var entrada = Captura(Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."));
        var primera = (await servicio.CapturarUnaVezAsync(_ana, entrada, "clave-173-cd", Ct)).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(primera.Folio, _ana, Arg.Any<CancellationToken>()).Returns(primera);

        var reintento = await servicio.CapturarUnaVezAsync(_ana, entrada, "clave-173-cd", Ct);
        var otroCuerpo = await servicio.CapturarUnaVezAsync(_ana, entrada with { ClienteNombre = "Otro" }, "clave-173-cd", Ct);

        reintento.Valor.Folio.Should().Be(primera.Folio);
        otroCuerpo.Error.Codigo.Should().Be(Cotifly.Aplicacion.Comun.clsCodigosAplicacion.IdempotencyKeyReutilizada, "D4: código estable, 409");
        await _nexo.Received(1).EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Almacén de idempotencia que nunca tiene nada guardado (como tras una petición que lanzó): ejecuta siempre la acción.</summary>
    private sealed class clsAlmacenSinMemoria : IClsAlmacenIdempotencia
    {
        public Task<clsResultado<T>> EjecutarUnaVezAsync<T>(
            string ambito, string clave, string huella, Func<CancellationToken, Task<clsResultado<T>>> accion,
            Func<clsResultado<T>, bool>? seGuarda, CancellationToken ct) => accion(ct);
    }

    private clsEnvioCosteoService ServicioIdempotente(IClsRegistroCapturasIdempotentes registro) => new(
        _cotizaciones, _politicas, _margen, _folios, _buscador, _nexo, _catalogos, _filas, _monedas, _unidad, _auditoria, _reloj,
        Options.Create(_opciones), NullLogger<clsEnvioCosteoService>.Instance, new clsPrecioCosteoService(),
        new clsCapturaRutasService(_filas, _costosConexos, new clsPlanificadorCamionSinVehiculo(), NullLogger<clsCapturaRutasService>.Instance),
        null, new clsAlmacenSinMemoria(), registro);

    private clsEnvioCosteoService Servicio() => new(
        _cotizaciones, _politicas, _margen, _folios, _buscador, _nexo, _catalogos, _filas, _monedas, _unidad, _auditoria, _reloj,
        Options.Create(_opciones), NullLogger<clsEnvioCosteoService>.Instance, new clsPrecioCosteoService(),
        new clsCapturaRutasService(_filas, _costosConexos, new clsPlanificadorCamionSinVehiculo(), NullLogger<clsCapturaRutasService>.Instance));

    /// <summary>Foto de Nexo con la única línea costeada (borrador, como el auto-costeo de la Biblioteca).</summary>
    private static clsCosteoConsultadoNexo FotoCosteada(decimal flete) =>
        new("EnCosteo", ["L1"], new clsCotizacionNexo("COT-9", clsCotizacionNexo.EstadoBorrador, "SinDefinir", [new clsLineaCosteoNexo("L1", flete, null, null)], []));
}
