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

    [Theory]
    [InlineData(EstadoCosteo.Autocosteo)]
    [InlineData(EstadoCosteo.Entregada)]
    public async Task HU0363_ConElCosteoListoSinFinalizarNoSeMarcaNiSeLlamaANexo(EstadoCosteo estado)
    {
        Estado(estado, null);
        _cotizacion.Vehiculos[0].RegistrarCosteo(estado, 1_000_000m, null);
        _cotizacion.RecalcularTotales(_reloj.AhoraUtc);
        _cotizacion.TieneCostoDisponible.Should().BeTrue();

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        resultado.Error.Mensaje.Should().StartWith($"Finaliza la cotización {_cotizacion.Folio} antes de marcar el resultado");
        _cotizacion.Ganada.Should().BeNull();
        _nexo.ReceivedCalls().Should().BeEmpty();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task HU0363_YaEnviadaAntesDeHu036ElResultadoNoExigeFinalizar()
    {
        // Sin backfill: una cotización enviada al cliente antes de HU-036 (sin finalizar) sigue marcando su resultado como siempre.
        _cotizacion.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Entregada, 2_000_000m, null);
        _cotizacion.RecalcularTotales(_reloj.AhoraUtc);
        _cotizacion.RegistrarEnvio();
        _cotizacion.Finalizada.Should().BeFalse();

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.Ganada.Should().BeTrue();
    }

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
    [InlineData(clsCodigosError.SolicitudNoTransicionable)]
    [InlineData(clsCodigosError.CotizacionNoEncontrada)]
    [InlineData(clsCodigosError.CosteoNexoRechazada)]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado)]
    public async Task SiNexoNoRespondeOkNoSeCambiaNadaLocal(string codigo)
    {
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "Nexo dijo que no"));
        _nexo.LeerCotizacionAsync(_ana, "SOL-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "SinDefinir", [], []));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct);

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

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct)).Valor.Ganada.Should().BeFalse();

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
        _nexo.LeerCotizacionAsync(_ana, "SOL-2", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new clsCotizacionNexo("COT-2", "Entregada", "SinDefinir", [], []));
        _nexo.MarcarResultadoAsync(_ana, "COT-2", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-2", "Entregada", "Perdida", [], []));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-2", Arg.Any<CancellationToken>()).Returns(FotoEntregada("COT-2"));

        var resultado = await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Valor muy alto", null, Ct);

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
        await _nexo.DidNotReceiveWithAnyArgs().LeerCotizacionAsync(default, default!, default, Ct);
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoSinEntregaLaGanadaQuedaPendienteDeSincronizarSinPedirElResultadoANexo()
    {
        // HU-033 (#199, decisión 6): antes era COTIZACION_NO_ENTREGADA; ahora se guarda solo en Cotifly y se envía cuando la Mesa entregue.
        Estado(EstadoCosteo.PendienteCosteo, _reloj.AhoraUtc.AddMinutes(5));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(
            new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoBorrador, "SinDefinir", [], [])));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, "Firmó el contrato", Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Pendiente);
        _cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");
        _cotizacion.ComentarioResultadoPendiente.Should().Be("Firmó el contrato");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "el estado de costeo no cambia");
        _cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("el polling sigue para ver la entrega");
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(clsResultadoCotizacionService.HerramientaResultadoPendiente, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task EnPendienteDeCosteoSinEntregaLaPerdidaSigueSiendoSoloEnCotifly()
    {
        // #127 (HU-013.17): el cliente desistió antes del costeo: no queda pendiente de sincronizar, se detiene el polling.
        Estado(EstadoCosteo.PendienteCosteo, _reloj.AhoraUtc.AddMinutes(5));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(
            new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoBorrador, "SinDefinir", [], [])));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, 0, Ct);

        resultado.Valor.Ganada.Should().BeFalse();
        _cotizacion.PerdidaSoloEnCotifly.Should().BeTrue();
        _cotizacion.SincronizacionResultado.Should().BeNull();
        _cotizacion.EstadoSincronizacionResultado.Should().Be("solo_cotifly");
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Theory]
    [InlineData("ganada")]
    [InlineData("perdida")]
    public async Task EnAutocosteoSiNexoNoLaTieneEntregadaElResultadoQuedaPendiente(string valor)
    {
        Estado(EstadoCosteo.Autocosteo, null);
        ConPrecio(EstadoCosteo.Autocosteo, 1_000_000m);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CotizacionNoEntregada, "Solo se puede marcar el resultado de una cotización entregada."));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, valor, "Valor muy alto", "Comentario", Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.Ganada.Should().Be(valor == "ganada");
        _cotizacion.ResultadoPendienteDeSincronizar.Should().BeTrue();
        _cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("si el polling ya no la consultaba, se vuelve a agendar");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
    }

    [Fact]
    public async Task UnSegundoResultadoIgualMientrasEstaPendienteEsIdempotenteYUnoDistintoEsResultadoYaDefinido()
    {
        Estado(EstadoCosteo.Autocosteo, null);
        ConPrecio(EstadoCosteo.Autocosteo, 1_000_000m);
        _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, _reloj.AhoraUtc);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct)).Valor.Ganada.Should().BeTrue();
        var distinto = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct);

        distinto.Error.Codigo.Should().Be(clsCodigosError.ResultadoYaDefinido);
        distinto.Error.Detalle.Should().Be("ganada");
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task ConElOkDeNexoElResultadoQuedaSincronizado()
    {
        await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        _cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Sincronizado);
        _cotizacion.ResultadoSincronizadoAt.Should().Be(_reloj.AhoraUtc);
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
        await _nexo.DidNotReceiveWithAnyArgs().LeerCotizacionAsync(default, default!, default, Ct);
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
        _nexo.LeerCotizacionAsync(_ana, "SOL-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "Ganada", [], []));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct);

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
        _nexo.LeerCotizacionAsync(_ana, "SOL-1", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "Ganada", [], []));

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct)).Error.Detalle.Should().Be("ganada");

        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    // HU-013.17 (#127): el cliente desiste antes del costeo.
    [Fact]
    public async Task EnErrorEnvioLaPerdidaQuedaSoloEnCotiflyConMotivoEIfMatch()
    {
        var cotizacion = SinCosteo(enError: true);

        var resultado = await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, versionEsperada: 0, Ct);

        resultado.Valor.Ganada.Should().BeFalse();
        cotizacion.MotivoRechazo.Should().Be("Cliente desistió del servicio");
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

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, versionEsperada: 0, Ct)).Valor.Ganada.Should().BeFalse();

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

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(codigo);

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
        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "  ", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsCodigosError.MotivoNoPermitido);
        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, Ct)).Error.Codigo.Should().Be(clsCodigosError.PrecondicionRequerida);
        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, versionEsperada: 7, Ct)).Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);

        cotizacion.Ganada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnaPerdidaSinCosteoYaRegistradaNoSeVuelveAMarcar()
    {
        var cotizacion = SinCosteo(enError: true);
        cotizacion.RegistrarPerdidaSinCosteo("Desistió", _reloj.AhoraUtc);

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Valor muy alto", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsCodigosError.ResultadoYaDefinido);
    }

    [Fact]
    public async Task SiLaPerdidaSinCosteoPierdeLaVersionEsConflicto()
    {
        var cotizacion = SinCosteo(enError: true);
        _unidad.GuardarCambiosSiVersionAsync(cotizacion.Id, 0, Arg.Any<CancellationToken>()).Returns(false);

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, versionEsperada: 0, Ct)).Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    // DEF-C-1 (E2E 2026-10-22): con RESULTADO_YA_DEFINIDO se lee la cotización de Nexo por su id conocido, sin "crear o recuperar".
    [Fact]
    public async Task ConResultadoYaDefinidoLeeLaCotizacionDeNexoConSuIdSinEscribir()
    {
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ResultadoYaDefinido, "Ya está marcada como Ganada"));
        _nexo.LeerCotizacionAsync(_ana, "SOL-1", "COT-1", Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", clsCotizacionNexo.EstadoEntregada, "Ganada", [], []));

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct)).Error.Detalle.Should().Be("ganada");

        await _nexo.Received(1).LeerCotizacionAsync(_ana, "SOL-1", "COT-1", Arg.Any<CancellationToken>());
    }

    // OBS-C-2 (E2E 2026-10-22): repetir el mismo resultado ya sincronizado no vuelve a escribir en Nexo.
    [Theory]
    [InlineData("ganada")]
    [InlineData("perdida")]
    public async Task RepetirElMismoResultadoYaSincronizadoEs200SinLlamarANexoNiGuardar(string resultado)
    {
        _cotizacion.MarcarSeguimiento(resultado == "ganada", resultado == "ganada" ? null : "Valor muy alto", _reloj.AhoraUtc);
        _cotizacion.MarcarResultadoSincronizado(_reloj.AhoraUtc);
        var version = _cotizacion.VersionNegocio;

        var repetido = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, resultado, "Valor muy alto", null, Ct);

        repetido.Valor.Should().BeSameAs(_cotizacion);
        _cotizacion.VersionNegocio.Should().Be(version, "200 sin cambios");
        _nexo.ReceivedCalls().Should().BeEmpty("el resultado ya está en Nexo");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    // E2E 2026-10-22: Nexo ya no reconoce la solicitud (404) en pendiente_costeo: la perdida queda solo en Cotifly, como en error_envio.
    [Fact]
    public async Task EnPendienteDeCosteoConLaSolicitudInexistenteEnNexoLaPerdidaQuedaSoloEnCotifly()
    {
        var cotizacion = SinCosteo(enError: false);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-9", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudNoEncontrada, "Nexo ya no reconoce la solicitud SOL-9."));

        var perdida = await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "Cliente desistió del servicio", null, versionEsperada: 0, Ct);

        perdida.Valor.Ganada.Should().BeFalse();
        cotizacion.PerdidaSoloEnCotifly.Should().BeTrue();
        cotizacion.EstadoSincronizacionResultado.Should().Be(clsCotizacionSolicitud.CodigoSoloCotifly);
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("el polling se detiene");
        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task EnPendienteDeCosteoConLaSolicitudInexistenteEnNexoLaGanadaNoSeRegistra()
    {
        var cotizacion = SinCosteo(enError: false);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-9", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudNoEncontrada, "Nexo ya no reconoce la solicitud SOL-9."));

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "ganada", null, null, versionEsperada: 0, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.CotizacionNoEntregada);

        cotizacion.Ganada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
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

    // HU-018.1 (#155, parte B): el motivo de una perdida es de la lista (la de Nexo) y "Otro" exige comentario.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("Precio alto")]
    public async Task HU0181_UnaPerdidaConUnMotivoFueraDeLaListaEs400SinLlamarANexo(string? motivo)
    {
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", motivo, "Comentario", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MotivoNoPermitido);
        resultado.Error.Datos!["motivos_permitidos"].Should().BeEquivalentTo(clsOpcionesResultado.MotivosPorDefecto);
        resultado.Error.Datos["requiere_comentario"].Should().Be(false);
        _cotizacion.Ganada.Should().BeNull();
        _nexo.ReceivedCalls().Should().BeEmpty("el motivo se valida antes de ir a Nexo");
    }

    [Fact]
    public async Task HU0181_ConElMotivoOtroSinComentarioEs400()
    {
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Otro", "   ", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MotivoNoPermitido);
        resultado.Error.Datos!["requiere_comentario"].Should().Be(true);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HU0181_ConOtroANexoVaElMotivoYElComentarioAparteYCotiflyGuardaAmbos()
    {
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", " otro ", " Cerró la planta ", Ct);

        resultado.Valor.Ganada.Should().BeFalse();
        _cotizacion.MotivoRechazo.Should().Be("Otro: Cerró la planta");
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => !r.Ganada && r.Motivo == "Otro" && r.Comentario == "Cerró la planta"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HU0181_ElMotivoSeComparaSinMayusculasYViajaConLaEscrituraDeLaLista()
    {
        await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "VALOR muy ALTO", "Pidió 5 % menos", Ct);

        _cotizacion.MotivoRechazo.Should().Be("Valor muy alto", "con otro motivo el comentario solo va a Nexo");
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => r.Motivo == "Valor muy alto" && r.Comentario == "Pidió 5 % menos"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HU0181_LaListaConfiguradaReemplazaALaDeNexo()
    {
        var opciones = new clsOpcionesResultado { MotivosPerdida = ["Plazo", "  ", "Otra razón"], MotivoConComentario = "Otra razón" };
        var catalogo = new clsCatalogoMotivosPerdida(Options.Create(opciones));

        (await Servicio(catalogo).MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Valor muy alto", null, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.MotivoNoPermitido, "la configurada reemplaza a la de Nexo");
        (await Servicio(catalogo).MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Otra razón", null, Ct)).Error.Datos!["requiere_comentario"]
            .Should().Be(true);
        (await Servicio(catalogo).MarcarAsync(_ana, _cotizacion.Folio, "perdida", "plazo", null, Ct)).Valor.MotivoRechazo.Should().Be("Plazo");
        catalogo.Listar().Motivos.Should().Equal(new clsMotivoPerdida("Plazo", false), new clsMotivoPerdida("Otra razón", true));
    }

    [Fact]
    public async Task HU0181_LaGanadaNoValidaElMotivoContraLaLista()
    {
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", "Mejor precio", null, Ct)).Valor.Ganada.Should().BeTrue();
    }

    [Fact]
    public async Task HU0181_UnaPerdidaConOtroPendienteGuardaElComentarioParaNexo()
    {
        Estado(EstadoCosteo.Autocosteo, null);
        ConPrecio(EstadoCosteo.Autocosteo, 1_000_000m);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CotizacionNoEntregada, "Solo se puede marcar el resultado de una cotización entregada."));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Otro", "Compró con la competencia", Ct);

        resultado.Valor.ResultadoPendienteDeSincronizar.Should().BeTrue();
        _cotizacion.MotivoRechazo.Should().Be("Otro: Compró con la competencia");
        _cotizacion.ComentarioResultadoPendiente.Should().Be("Compró con la competencia");
    }

    // Validación HU-018.1 (#155): el motivo se valida después de las respuestas que no escriben nada. Una descartada sigue siendo 409
    // COSTEO_NO_LISTO (lleva a /seguimiento) aunque la perdida venga sin motivo, en vez de un 400 que lo oculta.
    [Theory]
    [InlineData(EstadoCosteo.Descartada, null)]
    [InlineData(EstadoCosteo.CosteoVencido, "Precio alto")]
    public async Task HU0181_UnaDescartadaOVencidaSigueSiendoCosteoNoListoAunqueElMotivoNoSeaDeLaLista(EstadoCosteo estado, string? motivo)
    {
        Estado(estado, null);

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", motivo, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    // HU-033 / OBS-C-2: repetir una perdida ya registrada (sincronizada o pendiente) es idempotente aunque el reintento no traiga el motivo.
    [Theory]
    [InlineData(null)]
    [InlineData("Precio alto")]
    public async Task HU0181_RepetirUnaPerdidaYaSincronizadaSinMotivoDeLaListaSigueSiendo200SinCambios(string? motivo)
    {
        _cotizacion.MarcarSeguimiento(false, "Valor muy alto", _reloj.AhoraUtc);
        _cotizacion.MarcarResultadoSincronizado(_reloj.AhoraUtc);
        var version = _cotizacion.VersionNegocio;

        var repetido = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", motivo, null, Ct);

        repetido.Valor.Should().BeSameAs(_cotizacion);
        _cotizacion.MotivoRechazo.Should().Be("Valor muy alto", "el motivo guardado no cambia");
        _cotizacion.VersionNegocio.Should().Be(version);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HU0181_RepetirUnaPerdidaPendienteDeSincronizarSinMotivoSigueSiendo200SinCambios()
    {
        Estado(EstadoCosteo.Autocosteo, null);
        ConPrecio(EstadoCosteo.Autocosteo, 1_000_000m);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CotizacionNoEntregada, "Solo se puede marcar el resultado de una cotización entregada."));
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Otro", "Compró con la competencia", Ct)).EsExitoso.Should().BeTrue();
        var version = _cotizacion.VersionNegocio;
        _nexo.ClearReceivedCalls();

        var repetido = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", null, null, Ct);

        repetido.Valor.ResultadoPendienteDeSincronizar.Should().BeTrue();
        _cotizacion.MotivoRechazo.Should().Be("Otro: Compró con la competencia");
        _cotizacion.ComentarioResultadoPendiente.Should().Be("Compró con la competencia");
        _cotizacion.VersionNegocio.Should().Be(version);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HU0181_SinMotivoDeLaListaNoSeTocaLaCotizacionNiSeLeeElCosteoEnNexo()
    {
        // En pendiente_costeo la perdida lee primero el costeo de Nexo: el motivo inválido se rechaza antes de esa lectura.
        var cotizacion = SinCosteo(enError: false);

        (await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", "El cliente desistió", null, versionEsperada: 0, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.MotivoNoPermitido);

        _nexo.ReceivedCalls().Should().BeEmpty();
        cotizacion.Ganada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task HU0181_UnMotivoConLaTildeAparteEsElDeLaListaYANexoViajaConSuEscritura()
    {
        var descompuesto = "Cliente desistió del servicio".Normalize(System.Text.NormalizationForm.FormD);
        descompuesto.Should().NotBe("Cliente desistió del servicio");

        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", descompuesto, null, Ct)).EsExitoso.Should().BeTrue();

        _cotizacion.MotivoRechazo.Should().Be("Cliente desistió del servicio");
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => r.Motivo == "Cliente desistió del servicio"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Otro\u200B")] // con un espacio de ancho cero
    [InlineData("Otr\u043E")] // con la "o" cirílica
    [InlineData("Valor  muy alto")]
    [InlineData("Valor muy alto.")]
    public async Task HU0181_UnMotivoParecidoPeroDistintoNoSeAcepta(string motivo)
    {
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", motivo, "comentario", Ct)).Error.Codigo
            .Should().Be(clsCodigosError.MotivoNoPermitido);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    private clsResultadoCotizacionService Servicio(IClsCatalogoMotivosPerdida? motivos = null) => new(
        _cotizaciones, _nexo, _unidad, _auditoria, _reloj, new clsPrecioCosteoService(), Options.Create(new clsOpcionesCosteoNexo()), motivos);

    /// <summary>
    /// Una lectura anterior dejó la cotización en <paramref name="estado"/> (sin flete). HU-021: el estado de la cabecera es el resumen de
    /// sus camiones, así que primero lo toma el camión.
    /// </summary>
    private void Estado(EstadoCosteo estado, DateTimeOffset? siguiente)
    {
        _cotizacion.Vehiculos[0].RegistrarEstado(estado);
        _cotizacion.RegistrarConsultaCosteo(estado, "COT-1", siguiente);
    }

    /// <summary>
    /// El polling ya aplicó el costeo (el camión con su flete y la ruta y la solicitud con el precio, margen guardado) y, como lo hace el comercial antes de marcar el resultado (HU-036.3, #207), la cotización finalizada.
    /// </summary>
    private void ConPrecio(EstadoCosteo estado, decimal flete)
    {
        _cotizacion.Vehiculos[0].RegistrarCosteo(estado, flete, null);
        _cotizacion.RecalcularTotales(_reloj.AhoraUtc);
        _cotizacion.Finalizar(_ana, _reloj.AhoraUtc);
    }

    /// <summary>Foto de Nexo con la cotización entregada y la única línea costeada a 2.000.000.</summary>
    private static clsCosteoConsultadoNexo FotoEntregada(string cotizacionNexoId) =>
        new(clsCosteoConsultadoNexo.SolicitudEntregada, ["L1"],
            new clsCotizacionNexo(cotizacionNexoId, clsCotizacionNexo.EstadoEntregada, "Ganada", [new clsLineaCosteoNexo("L1", 2_000_000m, null, null)], []));
}
