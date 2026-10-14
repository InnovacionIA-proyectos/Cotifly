using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// <see cref="clsCubicacionLicitacionService"/> (HU-023.5, #160; B2 y E1 de #174) sin base de datos: el cupo del cubicador, el reclamo de
/// la ruta, la ruta cubicada, la caída que se reintenta, el rechazo que queda fallido, el reintento del comercial con y sin respaldo, el
/// envío único a Nexo (también cuando el candado del envío está ocupado) y la auditoría en <c>EventosHerramienta</c> sin montos.
/// </summary>
public sealed class clsCubicacionLicitacionServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsPlanificadorCamiones _planificador = Substitute.For<IClsPlanificadorCamiones>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsEnvioCosteoService _envio = Substitute.For<IClsEnvioCosteoService>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsCupoCubicador _cupos = Substitute.For<IClsCupoCubicador>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IAsyncDisposable _cupo = Substitute.For<IAsyncDisposable>();
    private readonly IAsyncDisposable _candadoEnvio = Substitute.For<IAsyncDisposable>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsLoggerFalso<clsCubicacionLicitacionService> _logger = new();
    private readonly clsCotizacionSolicitud _licitacion;

    public clsCubicacionLicitacionServiceTests()
    {
        _licitacion = Licitacion(rutas: 2);
        _cupos.IntentarTomarAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAsyncDisposable?>(_cupo));
        _cupos.IntentarTomarEnvioAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAsyncDisposable?>(_candadoEnvio));
        _cotizaciones.ReclamarRutaPorCubicarAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cotizaciones.ReclamarReenvioCosteoAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cotizaciones.ObtenerPorIdAsync(_licitacion.Id, Arg.Any<CancellationToken>()).Returns(_licitacion);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_licitacion.Folio, _usuario, Arg.Any<CancellationToken>()).Returns(_licitacion);
        _politicas.ObtenerPorIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((clsMargenPolitica?)null);
        Responder(Plan("3S3"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsCubicacionLicitacionService Servicio(int maximoIntentos = 3) => new(
        _cotizaciones, _planificador, _unidad, _envio, _politicas, _cupos, _reloj,
        Options.Create(new clsOpcionesCubicacionLicitacion { MaximoIntentos = maximoIntentos, EsperaReintentoSegundos = 30 }),
        _logger, _auditoria);

    /// <summary>Licitación guardada con sus rutas pendientes de cubicar (un camión provisional sin vehículo cada una).</summary>
    private static clsCotizacionSolicitud Licitacion(int rutas)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-AP-14102026-001", UsuarioId = _usuario, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.",
        }).Valor;
        cotizacion.RegistrarCaptura(
            new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [.. Enumerable.Range(0, rutas).Select(i => new clsRutaCapturada(
                clsSolicitudCosteoMuestra.RutaConCodigos(), 3, i, [new clsCamionPlaneado(null, null, 10_000m + i)], [], CubicacionPendiente: true))],
            reglasCaptura: false);
        return cotizacion;
    }

    private static clsPlanRuta Plan(params string[] vehiculos) =>
        new([.. vehiculos.Select(v => new clsCamionPlaneado(v, "FURGON", 10_000m, Guid.NewGuid()))],
            new clsCubicacionRuta(Guid.NewGuid(), "ASIGNADO", null, false));

    private void Responder(clsPlanRuta plan) =>
        _planificador.PlanificarAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPlanRuta>.Exito(plan));

    private void Fallar(string codigo) =>
        _planificador.PlanificarAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPlanRuta>.Fallo(new clsErrorDominio(codigo, "fallo de prueba")));

    private clsRutaPorCubicar Cola(int orden) =>
        new(_licitacion.Rutas.Single(r => r.Orden == orden).Id, _licitacion.Id, null);

    private clsLineaSolicitud Ruta(int orden) => _licitacion.Rutas.Single(r => r.Orden == orden);

    private Task EnviosANexo(int veces) =>
        _envio.Received(veces).EnviarGuardadaAsync(_licitacion, Arg.Any<clsMargenPolitica?>(), _usuario, Arg.Any<string?>());

    // ------------------------------------------------------------------ Cupo y reclamo

    [Fact]
    public async Task SinCupoLibreLaRutaEsperaSinReclamarlaNiLlamarAlCubicador()
    {
        _cupos.IntentarTomarAsync(4, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAsyncDisposable?>(null));

        (await Servicio().CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.SinCupo);

        await _cotizaciones.DidNotReceiveWithAnyArgs().ReclamarRutaPorCubicarAsync(default, default, default, Ct);
        await _planificador.DidNotReceiveWithAnyArgs().PlanificarAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task UnaRutaQueOtraReplicaYaReclamoNoSeCubicaYSueltaElCupo()
    {
        _cotizaciones.ReclamarRutaPorCubicarAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        (await Servicio().CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.NoReclamada);

        await _planificador.DidNotReceiveWithAnyArgs().PlanificarAsync(default, default, default!, Ct);
        await _cupo.Received(1).DisposeAsync();
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public async Task ElReclamoDuraCubicadorReclamoSegundosDesdeAhora()
    {
        var cola = Cola(1);
        await Servicio().CubicarAsync(cola, Ct);

        await _cotizaciones.Received(1).ReclamarRutaPorCubicarAsync(cola.RutaId, null, _reloj.AhoraUtc + TimeSpan.FromSeconds(120), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ Ruta cubicada y envío único

    [Fact]
    public async Task UnaRutaCubicadaQueNoEsLaUltimaGuardaSusCamionesYNoEnviaANexo()
    {
        Responder(Plan("3S3", "C2"));

        (await Servicio().CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.Cubicada);

        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Cubicada);
        Ruta(1).Vehiculos.Select(v => v.TipoVehiculo).Should().BeEquivalentTo(["3S3", "C2"], "los camiones del cubicador reemplazan al provisional");
        Ruta(2).EstadoCubicacion.Should().Be(EstadoCubicacion.Pendiente);
        await _unidad.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        await EnviosANexo(0);
        await _cupo.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task LaUltimaRutaCubicadaEnviaLaLicitacionANexoUnaSolaVezConElCandadoYElReclamo()
    {
        var servicio = Servicio();
        await servicio.CubicarAsync(Cola(1), Ct);

        (await servicio.CubicarAsync(Cola(2), Ct)).Should().Be(ResultadoCubicacionRuta.Cubicada);

        await EnviosANexo(1);
        await _cotizaciones.Received(1).ReclamarReenvioCosteoAsync(_licitacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _candadoEnvio.Received(2).DisposeAsync();
    }

    [Fact]
    public async Task SiOtraReplicaYaReclamoElEnvioNoSeEnviaOtraVez()
    {
        _cotizaciones.ReclamarReenvioCosteoAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var servicio = Servicio();
        await servicio.CubicarAsync(Cola(1), Ct);

        await servicio.CubicarAsync(Cola(2), Ct);

        await EnviosANexo(0);
    }

    [Fact]
    public async Task SiAlRecargarLaLicitacionYaSalioANexoNoSeReclamaNiSeEnvia()
    {
        var servicio = Servicio();
        await servicio.CubicarAsync(Cola(1), Ct);
        _cotizaciones.When(c => c.RecargarAsync(_licitacion, Arg.Any<CancellationToken>()))
            .Do(_ => _licitacion.RegistrarEnvioCosteo("SOL-OTRA-REPLICA", false, _reloj.AhoraUtc, _reloj.AhoraUtc));

        await servicio.CubicarAsync(Cola(2), Ct);

        await _cotizaciones.DidNotReceiveWithAnyArgs().ReclamarReenvioCosteoAsync(default, default, default, Ct);
        await EnviosANexo(0);
    }

    /// <summary>
    /// B2 (#174): las dos últimas rutas terminan juntas. La otra tiene el candado del envío (revisa, ve una ruta todavía pendiente y lo
    /// suelta). Esta ruta, que ya guardó la suya, no se va sin enviar: espera, toma el candado, revisa y envía una sola vez.
    /// </summary>
    [Fact]
    public async Task SiElCandadoDelEnvioEstaOcupadoEsperaVuelveARevisarYEnvia()
    {
        var servicio = Servicio();
        await servicio.CubicarAsync(Cola(1), Ct);
        _cupos.IntentarTomarEnvioAsync(_licitacion.Id, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IAsyncDisposable?>(null), Task.FromResult<IAsyncDisposable?>(null), Task.FromResult<IAsyncDisposable?>(_candadoEnvio));

        (await servicio.CubicarAsync(Cola(2), Ct)).Should().Be(ResultadoCubicacionRuta.Cubicada);

        await _cupos.Received(4).IntentarTomarEnvioAsync(_licitacion.Id, Arg.Any<CancellationToken>()); // 1 de la ruta 1 + 3 de la ruta 2
        await EnviosANexo(1);
    }

    [Fact]
    public async Task LaRecuperacionNoEsperaElCandadoDelEnvioSiOtroLoTiene()
    {
        foreach (var ruta in _licitacion.Rutas)
        {
            _licitacion.CompletarCubicacionDeRuta(ruta.Id, Plan("3S3"), _reloj.AhoraUtc);
        }

        _cotizaciones.ListarCubicadasSinEnviarAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([_licitacion.Id]);
        _cupos.IntentarTomarEnvioAsync(_licitacion.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAsyncDisposable?>(null));

        (await Servicio().EnviarCubicadasAsync(cancellationToken: Ct)).Should().Be(0);

        await _cupos.Received(1).IntentarTomarEnvioAsync(_licitacion.Id, Arg.Any<CancellationToken>());
        await EnviosANexo(0);
    }

    [Fact]
    public async Task LaRecuperacionEnviaLasLicitacionesYaCubicadasQueNoSalieron()
    {
        foreach (var ruta in _licitacion.Rutas)
        {
            _licitacion.CompletarCubicacionDeRuta(ruta.Id, Plan("3S3"), _reloj.AhoraUtc);
        }

        _cotizaciones.ListarCubicadasSinEnviarAsync(_reloj.AhoraUtc - clsCotizacionSolicitud.EsperaReenvioPendiente, 50, null, Arg.Any<CancellationToken>())
            .Returns([_licitacion.Id]);

        (await Servicio().EnviarCubicadasAsync(cancellationToken: Ct)).Should().Be(1);

        await EnviosANexo(1);
    }

    // ------------------------------------------------------------------ Fallos

    [Fact]
    public async Task UnaCaidaDelCubicadorSeReintentaConEsperaCrecienteYAlAgotarLosIntentosQuedaFallida()
    {
        Fallar(clsCodigosError.CubicadorNoDisponible);
        var servicio = Servicio(maximoIntentos: 2);

        (await servicio.CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.Reintento);
        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Pendiente);
        Ruta(1).CubicacionDisponibleAt.Should().Be(_reloj.AhoraUtc + TimeSpan.FromSeconds(30));
        Ruta(1).CubicacionIntentos.Should().Be(1);

        (await servicio.CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.Fallida);
        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Fallida);
        Ruta(1).FalloPorCaidaDelCubicador.Should().BeTrue();
        await EnviosANexo(0);
    }

    [Fact]
    public async Task UnRechazoDelCubicadorQuedaFallidoAlPrimerIntentoSinRespaldo()
    {
        Fallar(clsCodigosError.MercanciaDesconocida);

        (await Servicio().CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.Fallida);

        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Fallida);
        Ruta(1).ErrorCubicacion.Should().StartWith(clsCodigosError.MercanciaDesconocida);
        Ruta(1).FalloPorCaidaDelCubicador.Should().BeFalse();
        Ruta(1).CubicacionIntentos.Should().Be(1);
    }

    [Fact]
    public async Task SiLaLicitacionYaSalioANexoLaRutaPendienteQuedaFallidaSinLlamarAlCubicador()
    {
        _licitacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc);

        (await Servicio().CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.Fallida);

        await _planificador.DidNotReceiveWithAnyArgs().PlanificarAsync(default, default, default!, Ct);
    }

    // ------------------------------------------------------------------ Reintento del comercial

    [Fact]
    public async Task ReintentarSinRespaldoDevuelveLasRutasFallidasALaColaSinEnviar()
    {
        Fallar(clsCodigosError.MercanciaDesconocida);
        var servicio = Servicio();
        await servicio.CubicarAsync(Cola(1), Ct);

        var resultado = await servicio.ReintentarAsync(_usuario, _licitacion.Folio, usarRespaldo: false, Ct);

        resultado.EsExitoso.Should().BeTrue();
        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Pendiente);
        Ruta(1).CubicacionIntentos.Should().Be(0);
        Ruta(1).ErrorCubicacion.Should().BeNull();
        await EnviosANexo(0);
    }

    [Fact]
    public async Task ReintentarConRespaldoAceptaLaCaidaConElCamionSinVehiculoYSiNoQuedaNadaPendienteEnvia()
    {
        var servicio = Servicio(maximoIntentos: 1);
        await servicio.CubicarAsync(Cola(2), Ct);
        Fallar(clsCodigosError.CubicadorNoDisponible);
        (await servicio.CubicarAsync(Cola(1), Ct)).Should().Be(ResultadoCubicacionRuta.Fallida);

        var resultado = await servicio.ReintentarAsync(_usuario, _licitacion.Folio, usarRespaldo: true, Ct);

        resultado.EsExitoso.Should().BeTrue();
        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Cubicada);
        Ruta(1).Vehiculos.Should().ContainSingle().Which.TipoVehiculo.Should().BeNull("la Mesa de Costeo resuelve el vehículo");
        await EnviosANexo(1);
    }

    [Fact]
    public async Task ReintentarConRespaldoNoTapaUnRechazoDelCubicador()
    {
        Fallar(clsCodigosError.MercanciaDesconocida);
        var servicio = Servicio();
        await servicio.CubicarAsync(Cola(1), Ct);

        var resultado = await servicio.ReintentarAsync(_usuario, _licitacion.Folio, usarRespaldo: true, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        resultado.Error.Mensaje.Should().Contain("rechazó el cubicador");
        Ruta(1).EstadoCubicacion.Should().Be(EstadoCubicacion.Fallida);
        await EnviosANexo(0);
    }

    [Fact]
    public async Task ReintentarUnFolioAjenoOSinRutasFallidasNoCambiaNada()
    {
        var servicio = Servicio();

        (await servicio.ReintentarAsync(Guid.NewGuid(), _licitacion.Folio, false, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        (await servicio.ReintentarAsync(_usuario, _licitacion.Folio, false, Ct)).Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    // ------------------------------------------------------------------ Auditoría (E1)

    [Fact]
    public async Task CadaRutaProcesadaQuedaAuditadaSinMontosNiPesos()
    {
        await Servicio().CubicarAsync(Cola(1), Ct);
        Fallar(clsCodigosError.MercanciaDesconocida);
        await Servicio().CubicarAsync(Cola(2), Ct);

        await _auditoria.Received(1).RegistrarAsync(clsCubicacionLicitacionService.HerramientaCubicarRuta,
            Arg.Is<object?>(e => Json(e).Contains("\"ruta_orden\":1") && Json(e).Contains(_licitacion.Folio)),
            Arg.Is<object?>(s => Json(s).Contains("\"resultado\":\"cubicada\"") && Json(s).Contains("\"camiones\":1")),
            true, Arg.Any<int>(), Arg.Any<string?>());
        await _auditoria.Received(1).RegistrarAsync(clsCubicacionLicitacionService.HerramientaCubicarRuta,
            Arg.Is<object?>(e => Json(e).Contains("\"ruta_orden\":2")),
            Arg.Is<object?>(s => Json(s).Contains("\"resultado\":\"fallida\"") && Json(s).Contains(clsCodigosError.MercanciaDesconocida)),
            false, Arg.Any<int>(), Arg.Any<string?>());
        _auditoria.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<object>().Select(Json)
            .Should().NotContain(j => j.Contains("peso", StringComparison.OrdinalIgnoreCase) || j.Contains("costo") || j.Contains("precio"));
    }

    [Fact]
    public async Task ElReintentoDelComercialQuedaAuditadoConSuResultado()
    {
        var servicio = Servicio();
        await servicio.ReintentarAsync(_usuario, _licitacion.Folio, false, Ct);

        await _auditoria.Received(1).RegistrarAsync(clsCubicacionLicitacionService.HerramientaReintentar,
            Arg.Is<object?>(e => Json(e).Contains("\"usar_respaldo\":false")),
            Arg.Is<object?>(s => Json(s).Contains(clsCodigosError.EstadoNoPermite)),
            false, Arg.Any<int>(), Arg.Any<string?>());
    }

    private static string Json(object? valor) => valor is null ? string.Empty : clsAuditoriaService.AJson(valor);
}
