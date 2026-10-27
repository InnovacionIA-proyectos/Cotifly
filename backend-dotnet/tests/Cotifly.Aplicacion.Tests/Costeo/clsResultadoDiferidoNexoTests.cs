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
/// HU-033 (#199, ADR-023, decisión 6): resultado ganada/perdida diferido a Nexo. Marcado cuando Nexo todavía no tiene la cotización
/// Entregada, queda "pendiente de sincronizar" solo en Cotifly; el polling, al ver la entrega, lo envía a Nexo UNA vez y lo deja
/// "sincronizado". Nunca se llama a <c>/entregar</c>. Descartada, vencida o con los intentos agotados queda "no_sincronizable".
/// </summary>
public sealed class clsResultadoDiferidoNexoTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsOpcionesCosteoNexo _opciones = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsResultadoDiferidoNexoTests()
    {
        _cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-22102026-001", UsuarioId = _ana }).Valor;
        _cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 3);
        _cotizacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc.AddSeconds(5));
        _cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        _reloj.Avanzar(TimeSpan.FromSeconds(8));
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ObtenerPorIdAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ReclamarConsultaCosteoAsync(_cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        // Nexo todavía no la entrega: el resultado se rechaza con COTIZACION_NO_ENTREGADA.
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CotizacionNoEntregada, "Solo se puede marcar el resultado de una cotización entregada."));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(Foto("EnCosteo", "Borrador"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoEntregadaPendienteEntregadaSincronizadoUnaSolaVez()
    {
        // 1. El polling la deja en autocosteo (Nexo en Borrador) y el comercial marca ganada: Nexo responde COTIZACION_NO_ENTREGADA.
        await Polling().ProcesarAsync(Pendiente(), Ct);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        var marcada = await Resultado().MarcarAsync(_ana, _cotizacion.Folio, "ganada", "Mejor tiempo", "Contrato firmado", Ct);

        marcada.EsExitoso.Should().BeTrue();
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");
        _cotizacion.ComentarioResultadoPendiente.Should().Be("Contrato firmado");

        // 2. Sigue sin entrega: el polling no envía nada a Nexo.
        _nexo.ClearReceivedCalls();
        await Polling().ProcesarAsync(Pendiente(), Ct);
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");

        // 3. La Mesa entrega en Nexo: el polling lo ve y envía el resultado (con el comentario guardado) UNA vez.
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(Foto(clsCosteoConsultadoNexo.SolicitudEntregada, "Entregada"));
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", "Entregada", "Ganada", [], []));
        await Polling().ProcesarAsync(Pendiente(), Ct);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("sincronizado");
        _cotizacion.ResultadoSincronizadoAt.Should().Be(_reloj.AhoraUtc);
        _cotizacion.ComentarioResultadoPendiente.Should().BeNull();
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => r.Ganada && r.Motivo == null && r.Comentario == "Contrato firmado"), Arg.Any<CancellationToken>());

        // 4. Otra vuelta (o un ciclo repetido): ya no se envía de nuevo.
        await Polling().ProcesarAsync(Pendiente(), Ct);
        await _nexo.ReceivedWithAnyArgs(1).MarcarResultadoAsync(default, default!, default!, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsSincronizacionResultadoNexoService.HerramientaSincronizacion, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnaPerdidaPendienteViajaConSuMotivo()
    {
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(false, "Precio alto", null, _reloj.AhoraUtc);
        Entregada();

        (await Sincronizacion().SincronizarAsync(_cotizacion, _reloj.AhoraUtc, Ct)).Should().BeTrue();

        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => !r.Ganada && r.Motivo == "Precio alto"), Arg.Any<CancellationToken>());
        _cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Sincronizado);
    }

    [Fact]
    public async Task SiNexoYaTieneOtroResultadoMandaNexoYQuedaAnotado()
    {
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        Entregada();
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ResultadoYaDefinido, "Ya está marcada como Perdida."));
        _nexo.LeerCotizacionAsync(_ana, "SOL-1", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new clsCotizacionNexo("COT-1", "Entregada", "Perdida", [], []));

        (await Sincronizacion().SincronizarAsync(_cotizacion, _reloj.AhoraUtc, Ct)).Should().BeTrue();

        _cotizacion.Ganada.Should().BeFalse("manda Nexo, como en /resultado");
        _cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Sincronizado);
        _cotizacion.ErrorSincronizacionResultado.Should().Contain("Nexo ya tenía la cotización como perdida");
    }

    [Fact]
    public async Task UnFalloDeNexoReprogramaYAlAgotarLosIntentosQuedaNoSincronizable()
    {
        _opciones.MaximoFallosSincronizacionResultado = 2;
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        Entregada();
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "Nexo caído"));

        await Sincronizacion().SincronizarAsync(_cotizacion, _reloj.AhoraUtc, Ct);

        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");
        _cotizacion.IntentosSincronizacionResultado.Should().Be(1);
        _cotizacion.ErrorSincronizacionResultado.Should().Contain("Nexo caído");
        _cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc + _opciones.IntervaloReintentoConexos);

        await Sincronizacion().SincronizarAsync(_cotizacion, _reloj.AhoraUtc, Ct);

        _cotizacion.EstadoSincronizacionResultado.Should().Be("no_sincronizable");
        _cotizacion.ErrorSincronizacionResultado.Should().Contain("tras 2 intentos");
    }

    /// <summary>
    /// Auditoría de la integración (#199): sin el id de la cotización de Nexo, la sincronización la LEE (DEF-C-1, nunca el POST de
    /// "crear o recuperar"). Si Nexo todavía no la tiene (<c>COTIZACION_NO_ENTREGADA</c>), sigue pendiente y no cuenta un fallo: no
    /// agota los intentos ni la deja no_sincronizable.
    /// </summary>
    [Fact]
    public async Task SinIdDeNexoYSinCotizacionEnNexoSiguePendienteSinContarFallo()
    {
        _opciones.MaximoFallosSincronizacionResultado = 1;
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        _cotizacion.Vehiculos[0].RegistrarEstado(EstadoCosteo.Entregada);
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, null, null);
        _cotizacion.CotizacionNexoId.Should().BeNull();
        _nexo.LeerCotizacionAsync(_ana, "SOL-1", null, Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CotizacionNoEntregada, "Nexo todavía no tiene la cotización de la solicitud SOL-1."));

        var cambio = await Sincronizacion().SincronizarAsync(_cotizacion, _reloj.AhoraUtc, Ct);

        cambio.Should().BeFalse();
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");
        _cotizacion.IntentosSincronizacionResultado.Should().Be(0);
        _cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc + _opciones.IntervaloReintentoConexos);
        await _nexo.DidNotReceive().MarcarResultadoAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiNexoDescartaLaSolicitudElResultadoQuedaSoloEnCotifly()
    {
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(
            new clsCosteoConsultadoNexo(clsCosteoConsultadoNexo.SolicitudDescartada, ["L1"], null));

        await Polling().ProcesarAsync(Pendiente(), Ct);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Descartada);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("no_sincronizable");
        _cotizacion.ErrorSincronizacionResultado.Should().Contain("descartó");
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task ConUnResultadoPendienteElPollingSigueMasAllaDeLaEdadMaximaDelCosteo()
    {
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        _reloj.Avanzar(_opciones.EdadMaxima + TimeSpan.FromHours(1));

        var resultado = await Polling().ProcesarAsync(Pendiente(), Ct);

        resultado.Should().NotBe(ResultadoConsultaCosteo.Detenida);
        _cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("sigue esperando la entrega de la Mesa");
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");

        _reloj.Avanzar(_opciones.EdadMaximaSincronizacionResultado);
        (await Polling().ProcesarAsync(Pendiente(), Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("no_sincronizable");
    }

    [Fact]
    public async Task ConNexoCaidoPasadaLaEdadMaximaUnResultadoPendienteNoPierdeLaAgenda()
    {
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        _reloj.Avanzar(_opciones.EdadMaxima + TimeSpan.FromHours(1));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "Nexo caído"));

        (await Polling().ProcesarAsync(Pendiente(), Ct)).Should().Be(ResultadoConsultaCosteo.Error);

        _cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("sigue esperando la entrega aunque Nexo no responda");
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");
    }

    [Fact]
    public async Task SiNexoYaNoReconoceLaSolicitudElResultadoPendienteQuedaNoSincronizable()
    {
        Autocosteo();
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudNoEncontrada, "404"));

        (await Polling().ProcesarAsync(Pendiente(), Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);

        _cotizacion.EstadoSincronizacionResultado.Should().Be("no_sincronizable");
        _cotizacion.ErrorSincronizacionResultado.Should().Contain("ya no reconoce");
    }

    [Fact]
    public void ElEstadoQueVeElFrontendDistingueSoloCotiflyDeSincronizadoYPendiente()
    {
        _cotizacion.EstadoSincronizacionResultado.Should().BeNull("sin resultado");
        _cotizacion.MarcarSeguimiento(true, null, _reloj.AhoraUtc);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("sincronizado", "un resultado anterior al 13 de una cotización en Nexo");
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");

        var chat = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CHAT", UsuarioId = _ana }).Valor;
        chat.MarcarSeguimiento(false, "Precio", _reloj.AhoraUtc);
        chat.EstadoSincronizacionResultado.Should().Be("solo_cotifly", "una cotización sin Nexo nunca sincroniza");
    }

    private void Autocosteo()
    {
        _cotizacion.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 2_000_000m, null);
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _reloj.AhoraUtc);
        _cotizacion.RecalcularTotales(_reloj.AhoraUtc);
    }

    private void Entregada()
    {
        _cotizacion.Vehiculos[0].RegistrarEstado(EstadoCosteo.Entregada);
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "COT-1", null);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", "Entregada", "Ganada", [], []));
    }

    /// <summary>La agenda vigente de la cotización (lo que leería el worker); vencida si no hay.</summary>
    private clsConsultaCosteoPendiente Pendiente() => new(_cotizacion.Id, _cotizacion.CosteoSiguienteConsultaAt ?? _reloj.AhoraUtc);

    private static clsCosteoConsultadoNexo Foto(string estadoSolicitud, string estadoCotizacion) =>
        new(estadoSolicitud, ["L1"], new clsCotizacionNexo("COT-1", estadoCotizacion, "SinDefinir", [new clsLineaCosteoNexo("L1", 2_000_000m, null, null)], []));

    private clsSincronizacionResultadoNexoService Sincronizacion() =>
        new(_nexo, _auditoria, Options.Create(_opciones), NullLogger<clsSincronizacionResultadoNexoService>.Instance);

    private clsPollingCosteoService Polling() => new(
        _cotizaciones, _nexo, _unidad, _auditoria, _reloj, Options.Create(_opciones), NullLogger<clsPollingCosteoService>.Instance,
        new clsPrecioCosteoService(), sincronizacion: Sincronizacion());

    private clsResultadoCotizacionService Resultado() => new(
        _cotizaciones, _nexo, _unidad, _auditoria, _reloj, new clsPrecioCosteoService(), Options.Create(_opciones));
}
