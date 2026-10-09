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
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsResultadoCotizacionServiceTests()
    {
        _cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-004", UsuarioId = _ana }).Valor;
        _cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 3);
        _cotizacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc);
        Estado(EstadoCosteo.Entregada, null);
        _cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(FotoEntregada("COT-1"));
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", "Entregada", "Ganada", [], []));
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConElOkDeNexoSeGuardaGanadaConMotivoNoAplicaYANexoVanElMotivoYElComentario()
    {
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", "Mejor precio", "Nota interna", Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.MotivoRechazo.Should().Be(clsCotizacionSolicitud.MotivoRechazoNoAplica, "HU-021.13: una ganada no guarda motivo de rechazo");
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
        _nexo.ObtenerCotizacionAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "SinDefinir", [], []));

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
        Estado(EstadoCosteo.Autocosteo, _reloj.AhoraUtc.AddMinutes(1));
        ConPrecio(EstadoCosteo.Autocosteo, 1_000_000m);
        _cotizacion.PrecioFinal.Should().Be(1_176_471m);

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
        Estado(EstadoCosteo.Autocosteo, _reloj.AhoraUtc.AddMinutes(1));
        _cotizacion.PrecioFinal.Should().BeNull("el polling todavía no leyó el costo");

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.CostoFinal.Should().Be(2_000_000m);
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
        Estado(EstadoCosteo.Autocosteo, null);
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
        Estado(EstadoCosteo.Autocosteo, _reloj.AhoraUtc.AddMinutes(1));
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>()).Returns(false);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, 1, Arg.Any<CancellationToken>()).Returns(true);

        // HU-021: la recarga trae lo que guardó el polling (otro flete, 1.700.000 → 2.000.000): si la foto se reaplicara, volvería a 2.352.941.
        _cotizaciones.When(r => r.RecargarAsync(_cotizacion, Arg.Any<CancellationToken>())).Do(_ => ConPrecio(EstadoCosteo.Entregada, 1_700_000m));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.PrecioFinal.Should().Be(2_000_000m, "tras recargar ya trae el precio (del polling): no se vuelve a aplicar la foto");
        await _cotizaciones.Received(1).RecargarAsync(_cotizacion, Arg.Any<CancellationToken>());
        await _nexo.Received(1).ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinIdDeLaCotizacionDeNexoLoRecuperaYLoGuarda()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-005", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 3);
        cotizacion.RegistrarEnvioCosteo("SOL-2", true, _reloj.AhoraUtc, _reloj.AhoraUtc);
        cotizacion.CotizacionNexoId.Should().BeNull("autocosteo desde el alta: el polling aún no guardó la cotización de Nexo");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _nexo.ObtenerCotizacionAsync(_ana, "SOL-2", Arg.Any<CancellationToken>()).Returns(new clsCotizacionNexo("COT-2", "Entregada", "SinDefinir", [], []));
        _nexo.MarcarResultadoAsync(_ana, "COT-2", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-2", "Entregada", "Perdida", [], []));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-2", Arg.Any<CancellationToken>()).Returns(FotoEntregada("COT-2"));

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
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", new string('m', 101), null, Ct)).Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task UnaGanadaConMotivoDeMasDe100CaracteresNoSeRechazaYNoLeLlegaANexo()
    {
        // HU-021.13: en una ganada el motivo no se guarda ("No aplica"), así que su largo no se valida; a Nexo no se le envía (no cabe).
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", new string('m', 101), "Nota", Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.MotivoRechazo.Should().Be(clsCotizacionSolicitud.MotivoRechazoNoAplica);
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => r.Ganada && r.Motivo == null && r.Comentario == "Nota"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinEnviarANexoEsCotizacionNoEntregadaSinLlamarANexo()
    {
        Estado(EstadoCosteo.PendienteEnvio, null);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEntregada);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().ObtenerCotizacionAsync(default, default!, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoSinCostoEnNexoEsCotizacionNoEntregadaSinPedirElResultado()
    {
        Estado(EstadoCosteo.PendienteCosteo, null);
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
        Estado(EstadoCosteo.PendienteCosteo, null);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"));

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoConLaEntregaSinLeerSeMarcaYSeAplicaElPrecio()
    {
        // HU-013.14: Nexo costeó y entregó, y el polling (espaciado por edad) aún no lo vio.
        Estado(EstadoCosteo.PendienteCosteo, _reloj.AhoraUtc.AddMinutes(10));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        await _nexo.Received(1).ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>());
        await _nexo.DidNotReceiveWithAnyArgs().ObtenerCotizacionAsync(default, default!, Ct);
    }

    [Fact]
    public async Task ConLasLineasYaAsociadasElCosteoSaleDeLaRespuestaDelResultadoSinReleer()
    {
        Estado(EstadoCosteo.Autocosteo, _reloj.AhoraUtc.AddMinutes(1));
        _cotizacion.Vehiculos[0].AsignarLineaNexo("L1");
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(FotoEntregada("COT-1").Cotizacion!);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Valor.Ganada.Should().BeTrue();

        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    // HU-013.14: Nexo aceptó un resultado y Cotifly no lo guardó; luego el comercial pide el contrario.
    [Fact]
    public async Task SiNexoYaTieneOtroResultadoSeGuardaElDeNexoYSeRespondeElConflicto()
    {
        ConPrecio(EstadoCosteo.Entregada, 2_000_000m);
        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ResultadoYaDefinido, "Ya está marcada como Ganada"));
        _nexo.ObtenerCotizacionAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "Ganada", [], []));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Precio", null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ResultadoYaDefinido);
        resultado.Error.Detalle.Should().Be("ganada");
        resultado.Error.Mensaje.Should().Contain("Cotifly quedó con ese mismo resultado");
        _cotizacion.Ganada.Should().BeTrue("Cotifly queda igual que Nexo");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiNexoYaTieneElMismoResultadoNoSeEscribeNada()
    {
        _cotizacion.MarcarSeguimiento(true, null, _reloj.AhoraUtc);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ResultadoYaDefinido, "Ya está marcada como Ganada"));
        _nexo.ObtenerCotizacionAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "Ganada", [], []));

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", null, null, Ct)).Error.Detalle.Should().Be("ganada");

        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    // HU-013.17 (#127): el cliente desiste antes del costeo.
    [Fact]
    public async Task EnErrorEnvioLaPerdidaQuedaSoloEnCotiflyConMotivoEIfMatch()
    {
        var cotizacion = SinCosteo(enError: true);

        var resultado = await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "El cliente desistió", null, versionEsperada: 0, Ct);

        resultado.Valor.Ganada.Should().BeFalse();
        cotizacion.MotivoRechazo.Should().Be("El cliente desistió");
        cotizacion.PerdidaSoloEnCotifly.Should().BeTrue();
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio, "el estado de costeo no cambia");
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        cotizacion.VersionNegocio.Should().Be(1);
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(cotizacion.Id, 0, Arg.Any<CancellationToken>());
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsResultadoCotizacionService.HerramientaPerdidaSinCosteo, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task EnPendienteDeCosteoSinEntregaEnNexoLaPerdidaQuedaSoloEnCotiflyYDetieneElPolling()
    {
        var cotizacion = SinCosteo(enError: false);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-9", Arg.Any<CancellationToken>()).Returns(
            new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-9", clsCotizacionNexo.EstadoBorrador, "SinDefinir", [], [])));

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Desistió", null, versionEsperada: 0, Ct)).Valor.Ganada.Should().BeFalse();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("si Nexo costea después, el resultado no cambia");
        cotizacion.RequiereRelecturaCosteo.Should().BeFalse();
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Theory]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado)]
    public async Task EnPendienteDeCosteoConNexoSinResponderLaPerdidaEs503YNoSeRegistra(string codigo)
    {
        // Sin la confirmación de Nexo no se sabe si ya entregó: registrar la perdida solo en Cotifly dejaría los dos sistemas distintos.
        var cotizacion = SinCosteo(enError: false);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-9", Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(codigo, "Nexo no responde"));

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Desistió", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(codigo);

        cotizacion.Ganada.Should().BeNull();
        cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("el polling sigue");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task LaPerdidaSinCosteoExigeMotivoIfMatchYNoAdmiteGanada()
    {
        var cotizacion = SinCosteo(enError: true);

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "ganada", "x", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEntregada);
        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "  ", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);
        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Desistió", null, Ct)).Error.Codigo.Should().Be(clsCodigosError.PrecondicionRequerida);
        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Desistió", null, versionEsperada: 7, Ct)).Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);

        cotizacion.Ganada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnaPerdidaSinCosteoYaRegistradaNoSeVuelveAMarcar()
    {
        var cotizacion = SinCosteo(enError: true);
        cotizacion.RegistrarPerdidaSinCosteo("Desistió", _reloj.AhoraUtc);

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Otra", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsCodigosError.ResultadoYaDefinido);
    }

    [Fact]
    public async Task SiLaPerdidaSinCosteoPierdeLaVersionEsConflicto()
    {
        var cotizacion = SinCosteo(enError: true);
        _unidad.GuardarCambiosSiVersionAsync(cotizacion.Id, 0, Arg.Any<CancellationToken>()).Returns(false);

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Desistió", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    /// <summary>Cotización enviada a Nexo sin costeo (<c>pendiente_costeo</c>, solicitud SOL-9) o con el envío fallido (<c>error_envio</c>).</summary>
    private clsCotizacionSolicitud SinCosteo(bool enError)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = $"PCN-AP-28092026-{(enError ? 8 : 9)}", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 3);
        if (enError)
        {
            cotizacion.RegistrarErrorEnvioCosteo("Nexo no respondió");
        }
        else
        {
            cotizacion.RegistrarEnvioCosteo("SOL-9", false, _reloj.AhoraUtc, _reloj.AhoraUtc.AddSeconds(5));
        }

        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        return cotizacion;
    }

    [Theory]
    [InlineData(EstadoCosteo.Descartada)]
    [InlineData(EstadoCosteo.CosteoVencido)]
    public async Task UnaDescartadaOVencidaNoVaANexo(EstadoCosteo estado)
    {
        Estado(estado, null);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task ConResultadoYaMarcadoNoSeReintentaElEnvio()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-R1", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 3);
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

        var chat = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-006", UsuarioId = _ana }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(chat.Folio, _ana, Arg.Any<CancellationToken>()).Returns(chat);
        (await Servicio().MarcarAsync(_ana, chat.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }

    private clsResultadoCotizacionService Servicio() => new(
        _cotizaciones, _nexo, _unidad, _auditoria, _reloj, new clsPrecioCosteoService(), Options.Create(new clsOpcionesCosteoNexo()));

    /// <summary>
    /// Una lectura anterior dejó la cotización en <paramref name="estado"/> (sin flete). HU-021: el estado de la cabecera es el resumen de
    /// sus camiones, así que primero lo toma el camión.
    /// </summary>
    private void Estado(EstadoCosteo estado, DateTimeOffset? siguiente)
    {
        _cotizacion.Vehiculos[0].RegistrarEstado(estado);
        _cotizacion.RegistrarConsultaCosteo(estado, "COT-1", siguiente);
    }

    /// <summary>El polling ya aplicó el costeo: el camión con su flete y la ruta y la solicitud con el precio (margen guardado).</summary>
    private void ConPrecio(EstadoCosteo estado, decimal flete)
    {
        _cotizacion.Vehiculos[0].RegistrarCosteo(estado, flete, null);
        _cotizacion.RecalcularTotales(_reloj.AhoraUtc);
    }

    /// <summary>Foto de Nexo con la cotización entregada y la única línea costeada a 2.000.000.</summary>
    private static clsCosteoConsultadoNexo FotoEntregada(string cotizacionNexoId) =>
        new(clsCosteoConsultadoNexo.SolicitudEntregada, ["L1"],
            new clsCotizacionNexo(cotizacionNexoId, clsCotizacionNexo.EstadoEntregada, "Ganada", [new clsLineaCosteoNexo("L1", 2_000_000m, null, null)], []));
}
