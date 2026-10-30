using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
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
/// Polling del costeo (HU-005.6, ADR-015 §4): espaciado por edad, reclamo por cotización, derivación del estado, errores de Nexo
/// que no detienen el polling, edad máxima y auditoría de cada cambio.
/// </summary>
public sealed class clsPollingCosteoServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsOpcionesCosteoNexo _opciones = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;
    private readonly clsConsultaCosteoPendiente _pendiente;

    public clsPollingCosteoServiceTests()
    {
        _cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-001", UsuarioId = _ana }).Valor;
        _cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 3);
        _cotizacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc.AddSeconds(5));
        _pendiente = new clsConsultaCosteoPendiente(_cotizacion.Id, _reloj.AhoraUtc.AddSeconds(5));
        _reloj.Avanzar(TimeSpan.FromSeconds(8));
        _cotizaciones.ReclamarConsultaCosteoAsync(_cotizacion.Id, _pendiente.SiguienteConsultaAt, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        _cotizaciones.ObtenerPorIdAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SinFleteSigueEnPendienteDeCosteoYSeReprogramaSinAuditarCambio()
    {
        NexoResponde(Foto("EnCosteo", conFlete: false));

        var resultado = await Servicio().ProcesarAsync(_pendiente, Ct);

        resultado.Should().Be(ResultadoConsultaCosteo.SinCambio);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        // HU-021: el esquema 0009 ya no guarda la fecha de la última consulta (CosteoUltimaConsultaAt); queda la agenda.
        _cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc.AddSeconds(5), "al principio se pregunta cada 5 s");
        _cotizacion.CotizacionNexoId.Should().Be("COT-1");
        _cotizacion.VersionNegocio.Should().Be(0, "una consulta sin cambios (solo la agenda) no sube la versión de negocio");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>());
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public async Task ConTodasLasLineasCosteadasPasaAAutocosteoYElCambioQuedaEnLaAuditoria()
    {
        NexoResponde(Foto("EnCosteo", conFlete: true));

        var resultado = await Servicio().ProcesarAsync(_pendiente, Ct);

        resultado.Should().Be(ResultadoConsultaCosteo.Cambio);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        _cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull("en autocosteo se sigue consultando hasta la entrega o la edad máxima");
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaCambioEstado, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task EntregadaYDescartadaSonTerminalesYNoSeVuelvenAConsultar()
    {
        NexoResponde(Foto("Entregada", conFlete: true, estadoCotizacion: "Entregada"));

        await Servicio().ProcesarAsync(_pendiente, Ct);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        _cotizacion.SeConsultaEnNexo.Should().BeFalse();
    }

    [Fact]
    public async Task ProcesarDosVecesLaMismaFotoEsIdempotente()
    {
        NexoResponde(Foto("EnCosteo", conFlete: true));
        await Servicio().ProcesarAsync(_pendiente, Ct);
        var segunda = new clsConsultaCosteoPendiente(_cotizacion.Id, _cotizacion.CosteoSiguienteConsultaAt!.Value);
        _cotizaciones.ReclamarConsultaCosteoAsync(_cotizacion.Id, segunda.SiguienteConsultaAt, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);

        (await Servicio().ProcesarAsync(segunda, Ct)).Should().Be(ResultadoConsultaCosteo.SinCambio);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaCambioEstado, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    /// <summary>
    /// ADR-019 (Riesgo 2): una cotización migrada sin respaldo queda en <c>pendiente_costeo</c> sin el id de la solicitud de Nexo. El
    /// polling no la puede consultar: la deja en <c>error_envio</c> con <c>SOLICITUD_NEXO_DESCONOCIDA</c> (desde ahí funcionan el
    /// reintento, la re-solicitud y la perdida solo en Cotifly) y lo audita.
    /// </summary>
    [Fact]
    public async Task UnaPendienteDeCosteoSinIdDeNexoQuedaEnErrorDeEnvioConSuMotivo()
    {
        _cotizacion.PrepararReSolicitud();
        foreach (var ruta in _cotizacion.Rutas)
        {
            ruta.RegistrarEstadoCamiones(EstadoCosteo.PendienteCosteo);
        }

        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.PendienteCosteo, null, _pendiente.SiguienteConsultaAt);

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        _cotizacion.MotivoEstadoCosteo.Should().StartWith("SOLICITUD_NEXO_DESCONOCIDA");
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaCambioEstado, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SiOtraReplicaLaReclamoNoSeConsultaANexo()
    {
        _cotizaciones.ReclamarConsultaCosteoAsync(_cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.NoReclamada);

        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
    }

    [Fact]
    public async Task ElReclamoDuraMasQueLaConsultaANexo()
    {
        NexoResponde(Foto("EnCosteo", conFlete: false));

        await Servicio().ProcesarAsync(_pendiente, Ct);

        await _cotizaciones.Received(1).ReclamarConsultaCosteoAsync(_cotizacion.Id, _pendiente.SiguienteConsultaAt, _reloj.AhoraUtc.AddSeconds(120), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible, "HTTP 503")]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado, null)]
    [InlineData(clsCodigosError.CosteoNexoRechazada, "HTTP 403 SIN_PERMISO_CABINA_FLETES: Sin permiso.")]
    public async Task UnErrorDeNexoSeAuditaYSeReprogramaConEspaciadoSinCambiarElEstado(string codigo, string? detalle)
    {
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(codigo, "falló", detalle));

        var resultado = await Servicio().ProcesarAsync(_pendiente, Ct);

        resultado.Should().Be(ResultadoConsultaCosteo.Error);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        _cotizacion.CosteoConsultasFallidas.Should().Be(1);
        _cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc.AddSeconds(10), "tras un fallo la espera se duplica");
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaErrorConsulta, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnaDescartadaEnNexoEsTerminalYNoSeVuelveAConsultar()
    {
        NexoResponde(new clsCosteoConsultadoNexo("Descartada", [], null));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Cambio);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Descartada);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public async Task UnIdQueNexoYaNoReconoceTerminaElPollingConUnMotivoClaro()
    {
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudNoEncontrada, "No existe", "HTTP 404 SOLICITUD_NO_ENCONTRADA"));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        _cotizacion.MotivoEstadoCosteo.Should().Contain("ya no reconoce");
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("no se reintenta 72 h");
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaErrorConsulta, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaCambioEstado, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task PasadaLaEdadMaximaSinCosteoQuedaVencidaVisibleYNoSeConsultaMas()
    {
        _reloj.Avanzar(TimeSpan.FromHours(73));

        var resultado = await Servicio().ProcesarAsync(_pendiente, Ct);

        resultado.Should().Be(ResultadoConsultaCosteo.Detenida);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        _cotizacion.MotivoEstadoCosteo.Should().Contain("72 h");
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaCambioEstado, Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnaCotizacionQueYaNoSeSigueSaleDeLaAgenda()
    {
        EstadoLeido(EstadoCosteo.Descartada, null, _reloj.AhoraUtc);

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);

        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public async Task LaAgendaPideElLoteConfigurado()
    {
        _opciones.Lote = 7;

        await Servicio().PendientesAsync(Ct);

        await _cotizaciones.Received(1).ListarConsultasCosteoPendientesAsync(_reloj.AhoraUtc, 7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(60, 0, 15)]
    [InlineData(3600, 0, 900)]
    [InlineData(20, 1, 10)]
    [InlineData(20, 3, 40)]
    [InlineData(20, 9, 160)]
    [InlineData(36000, 2, 900)]
    public void ElEspaciadoCreceConLaEdadYConLosFallosHastaElMaximo(int edadSegundos, int fallos, int esperaSegundos)
    {
        var ahora = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        clsAgendaPollingCosteo.Espera(ahora, ahora.AddSeconds(-edadSegundos), fallos, _opciones).Should().Be(TimeSpan.FromSeconds(esperaSegundos));
    }

    [Fact]
    public void PasadaLaEdadMaximaNoHaySiguienteConsulta()
    {
        var ahora = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        clsAgendaPollingCosteo.Siguiente(ahora, ahora.AddHours(-72), 0, _opciones).Should().BeNull();
        clsAgendaPollingCosteo.Siguiente(ahora, ahora.AddHours(-71), 0, _opciones).Should().Be(ahora.AddMinutes(15));
    }

    [Fact]
    public async Task UnCambioDePrecioSubeLaVersionDeNegocioYSeGuardaContraLaVersionLeida()
    {
        NexoResponde(Foto("EnCosteo", conFlete: true));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Cambio);

        _cotizacion.VersionNegocio.Should().Be(1);
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiElComercialCambioLaCotizacionMientrasSeConsultabaNoSeGuardaNiSeAudita()
    {
        NexoResponde(Foto("EnCosteo", conFlete: true));
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().ProcesarAsync(_pendiente, Ct);

        resultado.Should().Be(ResultadoConsultaCosteo.Conflicto, "se descarta y se vuelve a consultar en el siguiente ciclo");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public void EnDolaresElPollingConvierteConLaTrmFijadaDeLaCotizacion()
    {
        var usd = clsCotizacionMuestra.Formulario("PCN-USD-1", _ana, moneda: "USD", trm: 4000m);
        usd.RegistrarEnvioCosteo("SOL-2", false, _reloj.AhoraUtc, _reloj.AhoraUtc);
        usd.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");

        // HU-021: la lectura completa actualiza el camión (flete) y la ruta saca el precio con su margen.
        clsLecturaCosteo.AplicarFoto(usd, Foto("EnCosteo", conFlete: true), _reloj.AhoraUtc, _opciones, new clsPrecioCosteoService()).Should().BeTrue();

        usd.PrecioFinal.Should().Be(588.24m, "2.000.000 / 0,85 = 2.352.941 COP, / 4.000 (la TRM fijada) = 588,24 USD");
        usd.CostoFinal.Should().Be(2_000_000m, "el costo sigue en pesos");
    }

    // HU-013.14 (ADR-018 §17): el precio que llega después del resultado y la reparación de las entregadas sin precio.
    [Fact]
    public async Task ElPrecioQueLlegaDespuesDelResultadoSeAplicaAunqueYaEsteGanada()
    {
        _cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        EstadoLeido(EstadoCosteo.Autocosteo, "COT-1", _pendiente.SiguienteConsultaAt);
        _cotizacion.MarcarSeguimiento(true, null, _reloj.AhoraUtc);
        NexoResponde(Foto("Entregada", conFlete: true, estadoCotizacion: "Entregada"));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Cambio);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.PrecioFinal.Should().Be(2_352_941m, "el resultado no congela el precio: solo el PDF");
        _cotizacion.Ganada.Should().BeTrue();
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public async Task UnaEntregadaSinPrecioSeReleeUnaVezAunquePasaraLaEdadMaximaYTomaElPrecio()
    {
        _cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        EstadoLeido(EstadoCosteo.Entregada, "COT-1", _pendiente.SiguienteConsultaAt);
        _cotizacion.MarcarSeguimiento(true, null, _reloj.AhoraUtc);
        _cotizacion.EntregadaSinPrecio.Should().BeTrue("así la dejaba el defecto de HU-013.14");
        _reloj.Avanzar(TimeSpan.FromHours(100));
        NexoResponde(Foto("Entregada", conFlete: true, estadoCotizacion: "Entregada"));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Cambio);

        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("leída una vez, entregada vuelve a ser terminal");
        _cotizacion.EntregadaSinPrecio.Should().BeFalse();
    }

    [Fact]
    public async Task SiLaRelecturaDeUnaEntregadaSinPrecioFallaSeReintentaAunquePasaraLaEdadMaxima()
    {
        EstadoLeido(EstadoCosteo.Entregada, "COT-1", _pendiente.SiguienteConsultaAt);
        _reloj.Avanzar(TimeSpan.FromHours(100));
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Error);

        _cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc.AddMinutes(15), "con el espaciado máximo, sin abandonarla");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
    }

    [Fact]
    public async Task UnaRelecturaQueFallaSiempreSeDetieneTrasElTopeDeFallosConMotivo()
    {
        _opciones.MaximoFallosRelectura = 3;
        EstadoLeido(EstadoCosteo.Entregada, "COT-1", _pendiente.SiguienteConsultaAt);
        _cotizacion.RegistrarFalloConsultaCosteo(_pendiente.SiguienteConsultaAt);
        _cotizacion.RegistrarFalloConsultaCosteo(_pendiente.SiguienteConsultaAt);
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, "No existe", "HTTP 404 COTIZACION_NO_ENCONTRADA"));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);

        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("no se consulta para siempre");
        _cotizacion.MotivoEstadoCosteo.Should().Contain("3 intentos");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
    }

    [Fact]
    public async Task UnaPendienteConResultadoPasadaLaEdadMaximaSeLeeAntesDeVencerla()
    {
        // La relectura del resultado falló y la cotización quedó en pendiente_costeo con resultado; pasan las 72 h.
        _cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        _cotizacion.MarcarSeguimiento(true, null, _reloj.AhoraUtc);
        _reloj.Avanzar(TimeSpan.FromHours(80));
        NexoResponde(Foto("Entregada", conFlete: true, estadoCotizacion: "Entregada"));

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Cambio);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada, "se leyó antes de vencerla");
        _cotizacion.PrecioFinal.Should().Be(2_352_941m);
    }

    [Fact]
    public async Task UnaPendienteConResultadoQueSigueSinCosteoSeVenceDespuesDeLeerla()
    {
        _cotizacion.MarcarSeguimiento(true, null, _reloj.AhoraUtc);
        _reloj.Avanzar(TimeSpan.FromHours(80));
        NexoResponde(Foto("EnCosteo", conFlete: false));

        await Servicio().ProcesarAsync(_pendiente, Ct);

        await _nexo.Received(1).ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>());
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public async Task UnaEntregadaSinCotizacionEnLaFotoQuedaConMotivoYNoSeReagenda()
    {
        _cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        NexoResponde(new clsCosteoConsultadoNexo("Entregada", ["L1"], null));

        await Servicio().ProcesarAsync(_pendiente, Ct);

        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.PrecioFinal.Should().BeNull();
        _cotizacion.MotivoEstadoCosteo.Should().Be(clsLecturaCosteo.MotivoEntregadaSinPrecio, "con motivo, la reparación del arranque no la vuelve a agendar");
    }

    [Fact]
    public async Task UnaPerdidaSoloEnCotiflyNoSeConsultaANexo()
    {
        _cotizacion.RegistrarPerdidaSinCosteo("El cliente desistió", _reloj.AhoraUtc);

        (await Servicio().ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Detenida);

        await _nexo.DidNotReceiveWithAnyArgs().ConsultarCosteoAsync(default, default!, Ct);
        _cotizacion.Ganada.Should().BeFalse();
    }

    [Fact]
    public async Task LaReparacionAgendaLasEntregadasSinPrecioConElRelojDeLaAplicacion()
    {
        _cotizaciones.ReprogramarEntregadasSinPrecioAsync(_reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(3);

        (await Servicio().ReprogramarEntregadasSinPrecioAsync(Ct)).Should().Be(3);
    }

    /// <summary>
    /// Una lectura anterior dejó la cotización en <paramref name="estado"/> (sin flete). HU-021: el estado de la cabecera es el resumen
    /// de sus camiones, así que primero lo toma el camión.
    /// </summary>
    private void EstadoLeido(EstadoCosteo estado, string? cotizacionNexoId, DateTimeOffset? siguiente)
    {
        _cotizacion.Vehiculos[0].RegistrarEstado(estado);
        _cotizacion.RegistrarConsultaCosteo(estado, cotizacionNexoId, siguiente);
    }

    private void NexoResponde(clsCosteoConsultadoNexo foto) =>
        _nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(foto);

    private static clsCosteoConsultadoNexo Foto(string estado, bool conFlete, string estadoCotizacion = "Borrador") =>
        new(estado, ["L1"], new clsCotizacionNexo("COT-1", estadoCotizacion, "SinDefinir",
            conFlete ? [new clsLineaCosteoNexo("L1", 2_000_000m, null, null)] : [], []));

    private clsPollingCosteoService Servicio(IClsSituacionLineasNexo? situaciones = null) => new(
        _cotizaciones, _nexo, _unidad, _auditoria, _reloj, Options.Create(_opciones), NullLogger<clsPollingCosteoService>.Instance,
        new clsPrecioCosteoService(), situacionesNexo: situaciones);

    /// <summary>Foto sin flete cuya única línea trae (o no, Nexo anterior a #309) el estado y el motivo pendiente.</summary>
    private static clsCosteoConsultadoNexo FotoConSituacion(string? estadoLinea, string? motivo) =>
        Foto("EnCosteo", conFlete: false) with
        {
            Detalle = [new clsLineaSolicitudNexo("L1", "BUENAVENTURA → BOGOTA, D.C.", null, null, null, null) { EstadoLinea = estadoLinea, MotivoPendiente = motivo }],
        };

    [Fact]
    public async Task Hu181_ElPollingDejaAlaVistaElEstadoYElMotivoPendienteDeLaLineaDeCadaCamion()
    {
        var situaciones = new clsSituacionLineasNexoEnMemoria(_reloj);
        NexoResponde(FotoConSituacion("porCostear", "Línea sin vehículo: requiere consulta completa"));

        (await Servicio(situaciones).ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.SinCambio);

        var camion = _cotizacion.Vehiculos.Single();
        camion.LineaSolicitudNexoId.Should().Be("L1", "el emparejamiento no cambia");
        situaciones.Obtener(_cotizacion.Id).Should().BeEquivalentTo(new Dictionary<Guid, clsSituacionLineaNexo>
        {
            [camion.Id] = new("por_costear", "Línea sin vehículo: requiere consulta completa") { LineaNexoId = "L1" },
        });
        _cotizacion.VersionNegocio.Should().Be(0, "el motivo solo se muestra: no es un cambio de negocio ni se guarda");
    }

    [Fact]
    public async Task Hu181_ConUnNexoSinLosCamposNoHayNadaQueMostrarNiFalla()
    {
        var situaciones = new clsSituacionLineasNexoEnMemoria(_reloj);
        NexoResponde(FotoConSituacion(null, null));

        (await Servicio(situaciones).ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.SinCambio);

        _cotizacion.Vehiculos.Single().LineaSolicitudNexoId.Should().Be("L1");
        situaciones.Obtener(_cotizacion.Id).Should().BeNull();
    }

    [Fact]
    public async Task Hu181_SiLaFotoNoSeGuardaPorConflictoNoSeMuestraSuMotivo()
    {
        var situaciones = new clsSituacionLineasNexoEnMemoria(_reloj);
        NexoResponde(FotoConSituacion("porCostear", "Consulta completa"));
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        (await Servicio(situaciones).ProcesarAsync(_pendiente, Ct)).Should().Be(ResultadoConsultaCosteo.Conflicto);

        situaciones.Obtener(_cotizacion.Id).Should().BeNull();
    }
}
