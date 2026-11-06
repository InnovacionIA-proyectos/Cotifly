using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// #259: un conexo no se queda en <c>estado_nexo = pendiente</c> para siempre. La línea del camión se elige con al menos una línea de Nexo
/// (la primera por id), en una cotización que ya no se costea va al primer camión vigente con línea (variante 4a) o queda
/// <c>fallido_definitivo</c>, un conexo nunca intentado cuenta para el reintento y el polling no saca de la agenda una cotización con
/// conexos pendientes.
/// </summary>
public sealed class clsConexosHuerfanosNexoTests
{
    private static readonly Guid _ana = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsServiciosConexosNexo _nexo = Substitute.For<IClsServiciosConexosNexo>();
    private readonly IClsResolutorConexosCatalogo _resolutor = Substitute.For<IClsResolutorConexosCatalogo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly List<clsEnvioConexosNexo> _registrados = [];

    public clsConexosHuerfanosNexoTests()
    {
        _reloj.AhoraUtc.Returns(clsCotizacionMuestra.Ahora);
        _cotizaciones.ListarEnviosConexosNexoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<clsEnvioConexosNexo>)[.. _registrados]);
        _cotizaciones.When(c => c.RegistrarEnvioConexosNexo(Arg.Any<clsEnvioConexosNexo>())).Do(c => _registrados.Add(c.Arg<clsEnvioConexosNexo>()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsEnvioConexosNexoService Envio() => new(
        _cotizaciones, _nexo, _resolutor, _unidad, Options.Create(new clsOpcionesCosteoNexo()), _reloj, NullLogger<clsEnvioConexosNexoService>.Instance);

    // ------------------------------------------------------------------ La línea del camión: al menos una, la primera por id

    [Fact]
    public void ConVariasLineasDeNexoParaElMismoCamionSeTomaLaPrimeraPorIdNoLaPrimeraDeLaLista()
    {
        var camion = clsCotizacionMuestra.Costeada().Vehiculos[0];   // lineaSolicitudNexoId = L1
        var nexo = CotizacionNexo(("L1", "CL-b"), ("L2", "CL-0"), ("L1", "CL-a"));

        clsServiciosConexosCosteoService.LineaDeNexo(nexo, camion).Should().Be("CL-a", "antes exigía exactamente una y devolvía null");
    }

    [Fact]
    public void SinNingunaLineaDelCamionNoHayLineaYSinCamionSoloSiLaCotizacionTieneUnaUnica()
    {
        var camion = clsCotizacionMuestra.Costeada().Vehiculos[0];

        clsServiciosConexosCosteoService.LineaDeNexo(CotizacionNexo(("L2", "CL-2")), camion).Should().BeNull();
        clsServiciosConexosCosteoService.LineaDeNexo(CotizacionNexo(("L2", "CL-2")), null).Should().Be("CL-2");
        clsServiciosConexosCosteoService.LineaDeNexo(CotizacionNexo(("L1", "CL-1"), ("L2", "CL-2")), null).Should().BeNull();
    }

    // ------------------------------------------------------------------ Destino del envío diferido (variante 4a)

    [Fact]
    public void Variante4a_EntregadaSinLaLineaDelPrimerCamionVanAlPrimerCamionVigenteQueLaTiene()
    {
        var (cotizacion, ruta) = DosCamiones(EstadoCosteo.Entregada);

        clsEnvioConexosNexoService.LineaDestino(cotizacion, ruta, CotizacionNexo(("L2", "CL-2"))).Should().Be("CL-2");
    }

    [Fact]
    public void Variante4a_MientrasSeCosteaSeEsperaLaLineaDelPrimerCamion()
    {
        var (cotizacion, ruta) = DosCamiones(EstadoCosteo.Autocosteo);

        clsEnvioConexosNexoService.LineaDestino(cotizacion, ruta, CotizacionNexo(("L2", "CL-2"))).Should().BeNull("en costeo la línea del primer camión todavía puede aparecer");
    }

    [Fact]
    public void Variante4a_ConLaLineaDelPrimerCamionVanAEllaAunqueOtroTambienTenga()
    {
        var (cotizacion, ruta) = DosCamiones(EstadoCosteo.Entregada);

        clsEnvioConexosNexoService.LineaDestino(cotizacion, ruta, CotizacionNexo(("L2", "CL-2"), ("L1", "CL-1"))).Should().Be("CL-1");
    }

    [Fact]
    public async Task EntregadaSinNingunCamionConLineaLosConexosQuedanFallidoDefinitivoSinPost()
    {
        var cotizacion = ConConexo(EstadoCosteo.Entregada);

        var resultado = await Envio().EnviarPendientesAsync(cotizacion, CotizacionNexo(("OTRA", "CL-9")), _ana, Ct);

        resultado.PorReintentar.Should().Be(0);
        _registrados.Should().ContainSingle().Which.Should().Match<clsEnvioConexosNexo>(r =>
            r.Estado == clsEnvioConexosNexo.FallidoDefinitivo && r.Conexos.Single() == "CX-1");
        await _nexo.DidNotReceiveWithAnyArgs().AgregarAsync(default, default!, default!, Ct);
        await _unidad.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        clsEnvioConexosNexoService.EstadoVisible(clsEnvioConexosNexoService.Estados(_registrados)["CX-1"])
            .Should().Be(clsEnvioConexosNexoService.VisibleFallidoDefinitivo, "no bloquea finalizar y el costeo avisa revisarlo en Nexo");
    }

    [Fact]
    public async Task EnCosteoSinLaLineaDelPrimerCamionSeEsperaSinRegistro()
    {
        var cotizacion = ConConexo(EstadoCosteo.Autocosteo);

        var resultado = await Envio().EnviarPendientesAsync(cotizacion, CotizacionNexo(("OTRA", "CL-9")), _ana, Ct);

        resultado.Should().Be(clsResultadoEnvioConexos.Nada);
        _registrados.Should().BeEmpty();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    // ------------------------------------------------------------------ Reintento de las que ya no se consultan

    [Fact]
    public async Task UnConexoNuncaIntentadoCuentaParaElReintentoSiLaCotizacionDeNexoExiste()
    {
        (await Envio().TieneReintentosAsync(ConConexo(EstadoCosteo.Entregada), Ct)).Should().BeTrue("antes solo contaban fallido y en_espera");
    }

    [Fact]
    public async Task ConElConexoEnviadoNoHayReintento()
    {
        var cotizacion = ConConexo(EstadoCosteo.Entregada);
        _registrados.Add(new clsEnvioConexosNexo(cotizacion.Folio, "SOL-1", cotizacion.PrimeraRuta!.Id, 1, clsEnvioConexosNexo.Enviado, ["CX-1"], []));

        (await Envio().TieneReintentosAsync(cotizacion, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task SiNexoNoRespondeElReintentoUnConexoNuncaIntentadoQuedaEnEsperaConSuTope()
    {
        var cotizacion = ConConexo(EstadoCosteo.Entregada);
        _nexo.LeerCotizacionAsync(_ana, "COT-1", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Nexo no responde."));

        var resultado = await Envio().ReintentarAsync(cotizacion, _ana, Ct);

        resultado.PorReintentar.Should().Be(1, "sigue en la agenda con el espaciado de la espera");
        _registrados.Should().ContainSingle().Which.Estado.Should().Be(clsEnvioConexosNexo.EnEspera);
    }

    // ------------------------------------------------------------------ Invariante de agenda del polling

    [Theory]
    [InlineData(clsEnvioConexosNexoService.VisiblePendiente, true)]
    [InlineData(clsEnvioConexosNexoService.VisibleEnviado, false)]
    [InlineData(clsEnvioConexosNexoService.VisibleFallidoDefinitivo, false)]
    public async Task ElReintentoNoSacaDeLaAgendaUnaCotizacionConConexosPendientes(string estadoVisible, bool sigueAgendada)
    {
        var cotizacion = ConConexo(EstadoCosteo.Entregada);
        cotizacion.ProgramarRelecturaCosteo(clsCotizacionMuestra.Ahora);
        var envio = Substitute.For<IClsEnvioConexosNexoService>();
        envio.TieneReintentosAsync(cotizacion, Arg.Any<CancellationToken>()).Returns(true);
        envio.ReintentarAsync(cotizacion, _ana, Arg.Any<CancellationToken>()).Returns(clsResultadoEnvioConexos.Nada);
        envio.EstadosAsync(cotizacion, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), string> { [(cotizacion.PrimeraRuta!.Id, "CX-1")] = estadoVisible });
        _cotizaciones.ReclamarConsultaCosteoAsync(cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cotizaciones.ObtenerPorIdAsync(cotizacion.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        var polling = new clsPollingCosteoService(_cotizaciones, Substitute.For<IClsCosteoNexo>(), _unidad, Substitute.For<IClsAuditoriaService>(), _reloj,
            Options.Create(new clsOpcionesCosteoNexo()), NullLogger<clsPollingCosteoService>.Instance, new clsPrecioCosteoService(), envio);

        var resultado = await polling.ProcesarAsync(new clsConsultaCosteoPendiente(cotizacion.Id, clsCotizacionMuestra.Ahora), Ct);

        resultado.Should().Be(sigueAgendada ? ResultadoConsultaCosteo.SinCambio : ResultadoConsultaCosteo.Detenida);
        cotizacion.CosteoSiguienteConsultaAt.Should().Be(sigueAgendada ? clsCotizacionMuestra.Ahora + new clsOpcionesCosteoNexo().IntervaloReintentoConexos : null);
    }

    // ------------------------------------------------------------------ Apoyo

    /// <summary>Cotización costeada (un camión con la línea <c>L1</c>, cotización de Nexo <c>COT-1</c>) con el conexo <c>CX-1</c>.</summary>
    private static clsCotizacionSolicitud ConConexo(EstadoCosteo estado) =>
        clsCotizacionMuestra.ConConexo(clsCotizacionMuestra.Costeada(estado: estado, usuarioId: _ana), "CX-1", "Cargue", 85_000m);

    /// <summary>Una ruta con dos camiones vigentes: el primero con la línea <c>L1</c> y el segundo con <c>L2</c>.</summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) DosCamiones(EstadoCosteo estado)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(estado: estado, usuarioId: _ana);
        var ruta = cotizacion.PrimeraRuta!;
        var segundo = ruta.AgregarVehiculo("2S2", null, 8_000m);
        cotizacion.AsignarLineaNexo(segundo.Id, "L2");
        ruta.VehiculosOrdenados.Select(v => v.LineaSolicitudNexoId).Should().Equal("L1", "L2");
        return (cotizacion, ruta);
    }

    /// <summary>Cotización de Nexo con una línea por (lineaSolicitudId, id de la línea de la cotización), en ese orden.</summary>
    private static clsCotizacionNexo CotizacionNexo(params (string LineaSolicitud, string Id)[] lineas) =>
        new("COT-1", clsCotizacionNexo.EstadoEntregada, "SinDefinir", [.. lineas.Select(l => new clsLineaCosteoNexo(l.LineaSolicitud, 1_000_000m, null, null, l.Id))], []);
}
