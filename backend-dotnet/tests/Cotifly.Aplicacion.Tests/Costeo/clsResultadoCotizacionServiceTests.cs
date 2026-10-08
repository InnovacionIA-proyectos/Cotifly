using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Resultado ganada/perdida en Nexo (HU-005.14, ADR-015 §6): dos escrituras, la local solo con 200 de Nexo; los 409 de Nexo y su
/// caída no cambian nada local; <c>comentario</c> solo se reenvía.
/// </summary>
public sealed class clsResultadoCotizacionServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacion _cotizacion;

    public clsResultadoCotizacionServiceTests()
    {
        _cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-004", UsuarioId = _ana }).Valor;
        _cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        _cotizacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc);
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "COT-1", _reloj.AhoraUtc, null);
        _cotizacion.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ListarLineasAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<clsCotizacionLineaSolicitud>());
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(FotoEntregada("COT-1"));
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", "Entregada", "Ganada", [], []));
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConElOkDeNexoSeGuardaGanadaYElMotivoPeroNoElComentario()
    {
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", "Mejor precio", "Nota interna", Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.MotivoRechazo.Should().Be("Mejor precio");
        _cotizacion.MarcadoAt.Should().Be(_reloj.AhoraUtc);
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => r.Ganada && r.Motivo == "Mejor precio" && r.Comentario == "Nota interna"), Arg.Any<CancellationToken>());
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>());
        _cotizacion.VersionNegocio.Should().Be(1, "el resultado es un cambio de negocio");
    }

    [Theory]
    [InlineData(clsCodigosError.ResultadoYaDefinido)]
    [InlineData(clsCodigosError.CotizacionNoEntregada)]
    [InlineData(clsCodigosError.SolicitudNoTransicionable)]
    [InlineData(clsCodigosError.CotizacionNoEncontrada)]
    [InlineData(clsCodigosError.CosteoNexoRechazada)]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado)]
    public async Task SiNexoNoRespondeOkNoSeCambiaNadaLocal(string codigo)
    {
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "Nexo dijo que no"));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Precio alto", null, Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        _cotizacion.Ganada.Should().BeNull();
        _cotizacion.MotivoRechazo.Should().BeNull();
        _cotizacion.MarcadoAt.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsResultadoCotizacionService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task LaEntregaImplicitaConElPrecioYaAplicadoDejaLaCotizacionEntregadaSinReleerNexo()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _reloj.AhoraUtc, _reloj.AhoraUtc.AddMinutes(1));
        _cotizacion.RegistrarPrecioCosteo(1_000_000m, 1_176_471m, null, _reloj.AhoraUtc);

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        _cotizacion.PrecioFinal.Should().Be(1_176_471m, "con el precio aplicado no se relee el costeo");
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    // HU-013.14 (ADR-018 §17): Nexo entregó, Cotifly aún no lo leyó y el comercial marca el resultado.
    [Fact]
    public async Task EntregadaEnNexoSinLeerYLuegoGanadaAplicaElPrecioEnLaMismaEscritura()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _reloj.AhoraUtc, _reloj.AhoraUtc.AddMinutes(1));
        _cotizacion.PrecioFinal.Should().BeNull("el polling todavía no leyó el costo");

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.Costo.Should().Be(2_000_000m);
        _cotizacion.PrecioFinal.Should().Be(2_352_941m, "2.000.000 / 0,85 con el margen guardado");
        _cotizacion.CosteoListoAt.Should().Be(_reloj.AhoraUtc);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("entregada con precio es terminal");
        _cotizacion.VersionNegocio.Should().Be(1, "resultado y precio en UNA escritura con la versión de negocio");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>());
        await _nexo.Received(1).ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>());
            _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task UnaEntregadaSinPrecioTambienSeCompletaAlMarcarElResultado()
    {
        _cotizacion.EntregadaSinPrecio.Should().BeTrue();

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Precio alto", null, Ct)).Valor.Ganada.Should().BeFalse();

        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        _cotizacion.EntregadaSinPrecio.Should().BeFalse();
    }

    [Fact]
    public async Task SiLaRelecturaFallaElResultadoSeRegistraSinDejarlaEntregadaHuerfanaYElPollingLaCompleta()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _reloj.AhoraUtc, null);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "Nexo caído"));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue("Nexo ya tiene el resultado: Cotifly lo registra igual");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo, "sin leer el costo no se marca entregada");
        _cotizacion.PrecioFinal.Should().BeNull();
        _cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc, "queda agendada para el polling");
        _cotizacion.SeConsultaEnNexo.Should().BeTrue();
        await _auditoria.Received(1).RegistrarAsync(clsResultadoCotizacionService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SiElPollingGanoLaCarreraSeRecargaYNoSeReaplicaElCosteo()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _reloj.AhoraUtc, _reloj.AhoraUtc.AddMinutes(1));
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>()).Returns(false);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, 1, Arg.Any<CancellationToken>()).Returns(true);
        var lecturas = 0;
        _cotizaciones.ListarLineasAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            lecturas++;
            return Array.Empty<clsCotizacionLineaSolicitud>();
        });

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        lecturas.Should().Be(1, "tras recargar ya trae el precio (del polling): no se vuelve a aplicar la foto");
        await _cotizaciones.Received(1).RecargarAsync(_cotizacion, Arg.Any<CancellationToken>());
        await _nexo.Received(1).ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinIdDeLaCotizacionDeNexoLoRecuperaYLoGuarda()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-005", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarEnvioCosteo("SOL-2", true, _reloj.AhoraUtc, _reloj.AhoraUtc);
        cotizacion.CotizacionNexoId.Should().BeNull("autocosteo desde el alta: el polling aún no guardó la cotización de Nexo");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _nexo.ObtenerCotizacionAsync(_ana, "SOL-2", Arg.Any<CancellationToken>()).Returns(new clsCotizacionNexo("COT-2", "Entregada", "SinDefinir", [], []));
        _nexo.MarcarResultadoAsync(_ana, "COT-2", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-2", "Entregada", "Perdida", [], []));

        var resultado = await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", null, null, Ct);

        resultado.Valor.Ganada.Should().BeFalse();
        cotizacion.CotizacionNexoId.Should().Be("COT-2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("empate")]
    public async Task UnResultadoInvalidoEs400SinLlamarANexo(string? resultado)
    {
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, resultado, null, null, Ct)).Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", new string('m', 101), null, Ct)).Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task SinEnviarANexoEsCotizacionNoEntregadaSinLlamarANexo()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.PendienteEnvio, "COT-1", _reloj.AhoraUtc, null);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEntregada);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().ObtenerCotizacionAsync(default, default!, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoSinCostoEnNexoEsCotizacionNoEntregadaSinPedirElResultado()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.PendienteCosteo, "COT-1", _reloj.AhoraUtc, null);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(
            new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoBorrador, "SinDefinir", [], [])));

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEntregada);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
        _cotizacion.Ganada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoConNexoCaidoEs503SinCambioLocal()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.PendienteCosteo, "COT-1", _reloj.AhoraUtc, null);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"));

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoConLaEntregaSinLeerSeMarcaYSeAplicaElPrecio()
    {
        // HU-013.14: Nexo costeó y entregó, y el polling (espaciado por edad) aún no lo vio.
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.PendienteCosteo, "COT-1", _reloj.AhoraUtc, _reloj.AhoraUtc.AddMinutes(10));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoCosteo.Descartada)]
    [InlineData(EstadoCosteo.CosteoVencido)]
    public async Task UnaDescartadaOVencidaNoVaANexo(EstadoCosteo estado)
    {
        _cotizacion.RegistrarConsultaCosteo(estado, "COT-1", _reloj.AhoraUtc, null);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task ConResultadoYaMarcadoNoSeReintentaElEnvio()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-R1", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarErrorEnvioCosteo("Nexo caído");
        cotizacion.PuedeReenviarCosteo(DateTimeOffset.MaxValue).Should().BeTrue();

        cotizacion.MarcarSeguimiento(false, "Perdida antes de reintentar", _reloj.AhoraUtc);

        cotizacion.PuedeReenviarCosteo(DateTimeOffset.MaxValue).Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnaAjenaEsNoEncontradaYUnaSinNexoEsCosteoNoListo()
    {
        (await Servicio().MarcarAsync(Guid.NewGuid(), _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);

        var chat = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-006", UsuarioId = _ana }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(chat.Folio, _ana, Arg.Any<CancellationToken>()).Returns(chat);
        (await Servicio().MarcarAsync(_ana, chat.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }

    private clsResultadoCotizacionService Servicio() => new(
        _cotizaciones, _nexo, _unidad, _auditoria, _reloj,
        new clsPrecioCosteoService(new Cotifly.Aplicacion.Precio.Services.clsPrecioService(
            Substitute.For<Cotifly.Aplicacion.Precio.Interfaces.IClsProveedorTrm>(), Substitute.For<IClsCalendarioNegocio>())),
        Options.Create(new clsOpcionesCosteoNexo()));

    /// <summary>Foto de Nexo con la cotización entregada y la única línea costeada a 2.000.000.</summary>
    private static clsCosteoConsultadoNexo FotoEntregada(string cotizacionNexoId) =>
        new(clsCosteoConsultadoNexo.SolicitudEntregada, ["L1"],
            new clsCotizacionNexo(cotizacionNexoId, clsCotizacionNexo.EstadoEntregada, "Ganada", [new clsLineaCosteoNexo("L1", 2_000_000m, null, null)], []));
}
