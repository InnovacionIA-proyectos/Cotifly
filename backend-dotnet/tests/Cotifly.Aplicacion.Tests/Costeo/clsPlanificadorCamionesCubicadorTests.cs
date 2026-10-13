using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// <see cref="clsPlanificadorCamionesCubicador"/> (HU-021.5, #133; contrato de HU-023.1, #156): un camión por vehículo (flota mixta) con
/// el volumen y los porcentajes, "sin selección" y "condicionada" como resultado, los rechazos y la respuesta inválida sin respaldo, y el
/// respaldo (un camión sin vehículo) sin cubicador o cuando se cae.
/// </summary>
public sealed class clsPlanificadorCamionesCubicadorTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private static readonly Guid _rutaId = Guid.NewGuid();
    private readonly IClsCubicador _cubicador = Substitute.For<IClsCubicador>();
    private readonly clsLoggerFalso<clsPlanificadorCamionesCubicador> _logger = new();

    public clsPlanificadorCamionesCubicadorTests()
    {
        _cubicador.EstaConfigurado.Returns(true);
        _cubicador.RespaldoSiFalla.Returns(true);
        _cubicador.PresupuestoCaptura.Returns(TimeSpan.FromMinutes(1));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsLineaSolicitudCosteo Ruta() => clsSolicitudCosteoMuestra.RutaConCodigos();

    private static decimal? PesoRuta => clsPlanificadorCamionSinVehiculo.PesoTotal(Ruta());

    private static clsVehiculoRecomendado Vehiculo(string codigo, string carroceria, decimal peso, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), codigo, carroceria, peso, 40m, 80m, 70m);

    private static clsRecomendacionCubicador Asignado(params clsVehiculoRecomendado[] vehiculos) =>
        new(Guid.NewGuid(), clsResultadosRecomendacion.Asignado, null, false, [], vehiculos, [], null);

    [Fact]
    public async Task CreaUnCamionPorVehiculoDeLaFlotaMixtaConVolumenPorcentajesEIdDeLaRecomendacion()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        Responder(Asignado(new clsVehiculoRecomendado(id1, "3S3", "FURGON", 16_000.456m, 62.5m, 81.25m, 47.06m), Vehiculo("C2", "ESTACAS", 4_500m, id2)));

        var camiones = (await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct)).Valor;

        camiones.Should().Equal(
            new clsCamionPlaneado("3S3", "FURGON", 16_000.46m, id1, 62.5m, 81.25m, 47.06m),
            new clsCamionPlaneado("C2", "ESTACAS", 4_500m, id2, 40m, 80m, 70m));
    }

    [Fact]
    public async Task MandaElIdDeLaRutaLaClaseDeViajeElUsuarioExternoYElCodigoDeCadaMercancia()
    {
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)));
        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuario, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("ana", "Ana", "ana@x.co", null, DateTimeOffset.UtcNow, 987654).Valor);

        var planificador = new clsPlanificadorCamionesCubicador(_logger, _cubicador, usuarios: usuarios);
        await planificador.PlanificarAsync(_usuario, _rutaId, Ruta() with { ClaseViaje = " Carga Suelta " }, Ct);
        await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        await _cubicador.Received(1).RecomendarAsync(
            Arg.Is<clsPeticionRecomendacion>(p => p.IdLineaSolicitud == _rutaId.ToString("D") && p.ClaseViajeSeleccionada == "Carga Suelta"
                && p.UsuarioId == "987654" && p.Mercancias.Count == Ruta().Posiciones.Count
                && p.Mercancias.All(m => m.CodigoMercancia == clsSolicitudCosteoMuestra.CodigoMercancia)),
            Arg.Any<CancellationToken>());
        await usuarios.Received(1).ObtenerPorIdAsync(_usuario, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaMercanciaConElIdAnteriorSeTraduceAlCodigoConElCatalogo()
    {
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)));
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito(
            [new clsTipoMercanciaCubicador("CAFE", "Café", null, true, false, null, true, clsSolicitudCosteoMuestra.TipoMercancia)]));
        var planificador = Planificador();

        var camiones = await planificador.PlanificarAsync(_usuario, _rutaId, clsSolicitudCosteoMuestra.RutaCaptura(), Ct);
        await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), clsSolicitudCosteoMuestra.RutaCaptura(), Ct);

        camiones.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3");
        await _cubicador.Received(2).RecomendarAsync(Arg.Is<clsPeticionRecomendacion>(p => p.Mercancias.All(m => m.CodigoMercancia == "CAFE")),
            Arg.Any<CancellationToken>());
        await _cubicador.Received(1).ListarTiposMercanciaAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinSeleccionAutomaticaElCamionQuedaSinVehiculoConElPesoDeLaRutaParaLaMesa()
    {
        Responder(new clsRecomendacionCubicador(Guid.NewGuid(), clsResultadosRecomendacion.SinSeleccionAutomatica,
            clsMotivosSinSeleccion.Extradimensionado, false, [], [], [], new clsExtradimensionCubicador(true, ["ALTO"], null)));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        _logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Information && r.Mensaje.Contains("EXTRADIMENSIONADO"));
    }

    [Fact]
    public async Task UnaRutaCondicionadaNoEsErrorYLosCamionesSeCreanComoVienen()
    {
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)) with { Condicionada = true, MercanciasCondicionadas = [new("QUIMICOS", null)] });

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Select(c => (c.TipoVehiculo, c.PesoKg)).Should().Equal(("3S3", 16_000m));
        _logger.Registros.Should().Contain(r => r.Mensaje.Contains("condicionada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SinCubicadorConfiguradoUsaElRespaldoSinLlamarlo()
    {
        _cubicador.EstaConfigurado.Returns(false);

        var planificador = Planificador();
        var resultado = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);
        var sinCubicador = await new clsPlanificadorCamionesCubicador(new clsLoggerFalso<clsPlanificadorCamionesCubicador>())
            .PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        sinCubicador.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        await _cubicador.DidNotReceiveWithAnyArgs().RecomendarAsync(default!, Ct);
        _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Which.Mensaje.Should().Contain("CUBICADOR_SIN_CONFIGURAR");
    }

    [Fact]
    public async Task UnaRutaSinMercanciasOConUnaMercanciaSinCodigoUsaElRespaldoSinLlamarNiCortarLasDemas()
    {
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)));
        var planificador = Planificador();
        var migrada = Ruta() with { Posiciones = [.. Ruta().Posiciones.Select((p, i) => i == 0 ? p with { CodigoMercancia = null, IdTipoMercancia = null } : p)] };

        var sinMercancias = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta() with { Posiciones = [] }, Ct);
        var sinCodigo = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), migrada, Ct);
        var normal = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        sinMercancias.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().BeNull();
        sinCodigo.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        normal.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3", "una ruta sin código no corta el cubicador para las demás");
        await _cubicador.ReceivedWithAnyArgs(1).RecomendarAsync(default!, Ct);
    }

    [Fact]
    public async Task UnaCaidaUsaElRespaldoAvisaConCodigoEstableYNoVuelveALlamarEnLaMismaCaptura()
    {
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>()).Returns(Caida("HTTP 503"));
        var planificador = Planificador();

        var primera = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var segunda = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        primera.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        segunda.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        await _cubicador.ReceivedWithAnyArgs(1).RecomendarAsync(default!, Ct);
        var aviso = _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Subject;
        aviso.Mensaje.Should().Contain("CUBICADOR_RESPALDO").And.Contain("HTTP 503");
    }

    [Fact]
    public async Task LaLlaveRechazadaEsUnaCaidaQueVaAlRespaldo()
    {
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>()).Returns(
            new clsErrorDominio(clsCodigosError.CubicadorNoAutorizado, "llave", "HTTP 401") { Datos = new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true } });

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
    }

    [Theory]
    [InlineData("MERCANCIAS_REQUERIDAS")]
    [InlineData("MERCANCIA_DESCONOCIDA")]
    [InlineData("MERCANCIA_NO_CUBICABLE")]
    [InlineData("DIMENSIONES_INVALIDAS")]
    [InlineData("CUBICADOR_RESPUESTA_INVALIDA")]
    public async Task UnRechazoDelContratoOUnaRespuestaInvalidaNuncaVanAlRespaldo(string codigo)
    {
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "mensaje para el comercial", "detalle interno"));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Error.Codigo.Should().Be(codigo, "esconder un dato malo con un camión sin vehículo es el fallo silencioso");
        resultado.Error.Mensaje.Should().Be("mensaje para el comercial");
        resultado.Error.Detalle.Should().BeNull("el detalle interno solo va al log");
        _logger.Registros.Should().Contain(r => r.Mensaje.Contains("detalle interno"));
    }

    [Fact]
    public async Task UnRechazoNoPrevistoDeUnaRutaSoloAfectaASuRuta()
    {
        var llamadas = 0;
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++llamadas == 1
                ? clsResultado<clsRecomendacionCubicador>.Fallo(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "rechazo", "HTTP 409"))
                : clsResultado<clsRecomendacionCubicador>.Exito(Asignado(Vehiculo("3S3", "FURGON", 16_000m)))));
        var planificador = Planificador();

        var rechazada = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var siguiente = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        rechazada.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        siguiente.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3", "un 4xx no es una caída: la siguiente ruta sí llama");
        llamadas.Should().Be(2);
    }

    [Fact]
    public async Task AgotadoElPresupuestoLasRutasRestantesNoLlamanAlCubicador()
    {
        _cubicador.PresupuestoCaptura.Returns(TimeSpan.Zero);
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)));
        var planificador = Planificador();

        var primera = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var segunda = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);
        var tercera = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        primera.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3");
        segunda.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        tercera.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        await _cubicador.ReceivedWithAnyArgs(1).RecomendarAsync(default!, Ct);
        _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Which.Mensaje.Should().Contain("presupuesto");
    }

    [Fact]
    public async Task AgotadoElPresupuestoSinRespaldoLaCapturaFalla()
    {
        _cubicador.PresupuestoCaptura.Returns(TimeSpan.Zero);
        _cubicador.RespaldoSiFalla.Returns(false);
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)));
        var planificador = Planificador();

        await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var segunda = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        segunda.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
    }

    [Fact]
    public async Task SinRespaldoUnaCaidaDelCubicadorImpideLaCapturaSinExponerElDetalle()
    {
        _cubicador.RespaldoSiFalla.Returns(false);
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>())
            .Returns(Caida("tiempo agotado en POST /recomendaciones"));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Mensaje.Should().Be(clsPlanificadorCamionesCubicador.MensajeNoDisponible);
        resultado.Error.Detalle.Should().BeNull("el detalle interno solo va al log");
        resultado.Error.Datos.Should().BeNull();
        _logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("tiempo agotado"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnVehiculoFueraDeLosLimitesDeLaCapturaEsUnaRespuestaInvalida(int caso)
    {
        Responder(Asignado(caso == 0
            ? Vehiculo(new string('X', clsSolicitudCosteo.LargoCatalogo + 1), "FURGON", 1_000m)
            : Vehiculo("3S3", "FURGON", clsSolicitudCosteo.MaximoPeso + 1m)));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorRespuestaInvalida);
    }

    private static clsErrorDominio Caida(string detalle) =>
        new(clsCodigosError.CubicadorNoDisponible, "caído", detalle)
        {
            Datos = new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true },
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaBanderaDeRespaldoDecideSiElVehiculoDelClienteSeConservaOSeIgnora(bool aceptar)
    {
        // HU-021.11 (#138): Captura:AceptarVehiculoCliente (apagada por defecto, decisión 7).
        var logger = new clsLoggerFalso<clsPlanificadorCamionesCubicadorTests>();
        var rutas = new List<clsLineaLicitacionEntrada?> { new(null, Ruta() with { TipoVehiculo = "3S3", Carroceria = "FURGON" }), null };

        var resultado = clsEnvioCosteoService.SinVehiculo(rutas, logger, aceptar);

        resultado[0]!.Linea.TipoVehiculo.Should().Be(aceptar ? "3S3" : null);
        resultado[0]!.Linea.Carroceria.Should().Be(aceptar ? "FURGON" : null);
        resultado[1].Should().BeNull();
        logger.Registros.Should().ContainSingle(r => r.Nivel == (aceptar ? LogLevel.Information : LogLevel.Warning));
    }

    [Fact]
    public async Task ElCamionDelRespaldoLlevaElVehiculoDeLaRutaSoloSiLaBanderaLoConservo()
    {
        _cubicador.EstaConfigurado.Returns(false);
        var conVehiculo = Ruta() with { TipoVehiculo = " 3S3 ", Carroceria = "FURGON" };

        var camiones = (await Planificador().PlanificarAsync(_usuario, _rutaId, conVehiculo, Ct)).Valor;
        var sinVehiculo = (await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct)).Valor;

        camiones.Should().Equal(new clsCamionPlaneado("3S3", "FURGON", PesoRuta));
        sinVehiculo.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
    }

    [Fact]
    public async Task ConLaRecomendacionDelCubicadorElVehiculoDeLaRutaNoSeUsa()
    {
        var id = Guid.NewGuid();
        Responder(Asignado(Vehiculo("2S2", "ESTACAS", 12_500m, id)));

        var camiones = (await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta() with { TipoVehiculo = "3S3", Carroceria = "FURGON" }, Ct)).Valor;

        camiones.Should().Equal([new clsCamionPlaneado("2S2", "ESTACAS", 12_500m, id, 40m, 80m, 70m)], "si el cubicador recomienda, manda el cubicador");
    }

    private void Responder(clsRecomendacionCubicador recomendacion) =>
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>()).Returns(recomendacion);

    private clsPlanificadorCamionesCubicador Planificador() => new(_logger, _cubicador);
}
