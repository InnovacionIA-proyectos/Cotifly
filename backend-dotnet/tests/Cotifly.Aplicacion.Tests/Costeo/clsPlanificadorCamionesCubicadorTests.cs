using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// <see cref="clsPlanificadorCamionesCubicador"/> (HU-021.5, #133): un camión por fila de la recomendación (C2, C3), "sin selección" y
/// "condicionada" como resultado (C6) y el respaldo (un camión sin vehículo) sin cubicador o cuando falla.
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

    private static clsLineaSolicitudCosteo Ruta() => clsSolicitudCosteoMuestra.RutaCaptura();

    private static decimal? PesoRuta => clsPlanificadorCamionSinVehiculo.PesoTotal(Ruta());

    [Fact]
    public async Task CreaUnCamionPorFilaConVehiculoCarroceriaPesoEIdDeLaRecomendacion()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        Responder(new clsVehiculoRecomendado(id1, "3S3", " FURGON ", 16_000.456m, clsResultadosRecomendacion.Seleccionado),
            new clsVehiculoRecomendado(id2, "2S2", "ESTACAS", 12_500m, clsResultadosRecomendacion.Seleccionado));

        var camiones = (await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct)).Valor;

        camiones.Should().Equal(new clsCamionPlaneado("3S3", "FURGON", 16_000.46m, id1), new clsCamionPlaneado("2S2", "ESTACAS", 12_500m, id2));
        await _cubicador.Received(1).RecomendarAsync(_rutaId,
            Arg.Is<IReadOnlyList<clsMercanciaCubicador>>(m => m.Count == Ruta().Posiciones.Count && m.All(x => x.IdTipoMercancia == clsSolicitudCosteoMuestra.TipoMercancia)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinSeleccionAutomaticaElCamionQuedaSinVehiculoParaLaMesa()
    {
        var id = Guid.NewGuid();
        Responder(new clsVehiculoRecomendado(id, "3S3", "FURGON", 9_000m, clsResultadosRecomendacion.SinSeleccionAutomatica, "sin vehículo compatible"));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Should().Equal(new clsCamionPlaneado(null, null, 9_000m, id));
        _logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Information && r.Mensaje.Contains("sin vehículo compatible"));
    }

    [Fact]
    public async Task UnaMercanciaCondicionadaNoEsErrorYLosCamionesSeCreanComoVienen()
    {
        Responder(new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, clsResultadosRecomendacion.Condicionada, "AdminTarifas"),
            new clsVehiculoRecomendado(Guid.NewGuid(), null, null, 12_500m, clsResultadosRecomendacion.Condicionada));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Select(c => (c.TipoVehiculo, c.PesoKg)).Should().Equal(("3S3", 16_000m), (null, 12_500m));
        _logger.Registros.Should().Contain(r => r.Mensaje.Contains("condicionada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConUnaSolaFilaSinPesoUsaElPesoTotalDeLaRuta()
    {
        Responder(new clsVehiculoRecomendado(null, "3S3", "FURGON", null, clsResultadosRecomendacion.Seleccionado));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Valor.Should().Equal(new clsCamionPlaneado("3S3", "FURGON", PesoRuta, null));
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
        await _cubicador.DidNotReceiveWithAnyArgs().RecomendarAsync(default, default!, Ct);
        _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Which.Mensaje.Should().Contain("CUBICADOR_SIN_CONFIGURAR");
    }

    [Fact]
    public async Task UnaRutaSinMercanciasOConUnaMercanciaSinTipoUsaElRespaldoSinLlamarNiCortarLasDemas()
    {
        Responder(new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, clsResultadosRecomendacion.Seleccionado));
        var planificador = Planificador();
        var migrada = Ruta() with { Posiciones = [.. Ruta().Posiciones.Select((p, i) => i == 0 ? p with { IdTipoMercancia = null } : p)] };

        var sinMercancias = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta() with { Posiciones = [] }, Ct);
        var sinTipo = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), migrada, Ct);
        var normal = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        sinMercancias.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().BeNull();
        sinTipo.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        normal.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3", "una ruta sin tipo no corta el cubicador para las demás");
        await _cubicador.ReceivedWithAnyArgs(1).RecomendarAsync(default, default!, Ct);
    }

    [Fact]
    public async Task UnaCaidaUsaElRespaldoAvisaConCodigoEstableYNoVuelveALlamarEnLaMismaCaptura()
    {
        _cubicador.RecomendarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsMercanciaCubicador>>(), Arg.Any<CancellationToken>())
            .Returns(Caida("HTTP 503"));
        var planificador = Planificador();

        var primera = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var segunda = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        primera.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        segunda.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        await _cubicador.ReceivedWithAnyArgs(1).RecomendarAsync(default, default!, Ct);
        var aviso = _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Subject;
        aviso.Mensaje.Should().Contain("CUBICADOR_RESPALDO").And.Contain("HTTP 503");
    }

    [Fact]
    public async Task UnRechazoDeUnaRutaSoloAfectaASuRuta()
    {
        var llamadas = 0;
        _cubicador.RecomendarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsMercanciaCubicador>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(++llamadas == 1
                ? clsResultado<clsRecomendacionCubicador>.Fallo(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "rechazo", "HTTP 400"))
                : clsResultado<clsRecomendacionCubicador>.Exito(new clsRecomendacionCubicador(
                    [new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, clsResultadosRecomendacion.Seleccionado)]))));
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
        Responder(new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, clsResultadosRecomendacion.Seleccionado));
        var planificador = Planificador();

        var primera = await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var segunda = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);
        var tercera = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        primera.Valor.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3");
        segunda.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        tercera.Valor.Should().Equal(new clsCamionPlaneado(null, null, PesoRuta));
        await _cubicador.ReceivedWithAnyArgs(1).RecomendarAsync(default, default!, Ct);
        _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Which.Mensaje.Should().Contain("presupuesto");
    }

    [Fact]
    public async Task AgotadoElPresupuestoSinRespaldoLaCapturaFalla()
    {
        _cubicador.PresupuestoCaptura.Returns(TimeSpan.Zero);
        _cubicador.RespaldoSiFalla.Returns(false);
        Responder(new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, clsResultadosRecomendacion.Seleccionado));
        var planificador = Planificador();

        await planificador.PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);
        var segunda = await planificador.PlanificarAsync(_usuario, Guid.NewGuid(), Ruta(), Ct);

        segunda.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
    }

    [Fact]
    public async Task SinRespaldoUnaFallaDelCubicadorImpideLaCapturaSinExponerElDetalle()
    {
        _cubicador.RespaldoSiFalla.Returns(false);
        _cubicador.RecomendarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsMercanciaCubicador>>(), Arg.Any<CancellationToken>())
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
    [InlineData(2)]
    [InlineData(3)]
    public async Task UnaRespuestaInvalidaEsUnaFallaDelCubicador(int caso)
    {
        _cubicador.RespaldoSiFalla.Returns(false);
        clsVehiculoRecomendado[] filas = caso switch
        {
            0 => [],
            1 => [new(null, "3S3", "FURGON", 10_000m, clsResultadosRecomendacion.Seleccionado), new(null, "2S2", "ESTACAS", null, clsResultadosRecomendacion.Seleccionado)],
            2 => [new(null, "3S3", "FURGON", -1m, clsResultadosRecomendacion.Seleccionado)],
            _ => [new(null, new string('X', clsSolicitudCosteo.LargoCatalogo + 1), "FURGON", 1_000m, clsResultadosRecomendacion.Seleccionado)],
        };
        Responder(filas);

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Ruta(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Detalle.Should().BeNull();
        _logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("respuesta inválida"));
    }

    private static clsErrorDominio Caida(string detalle) =>
        new(clsCodigosError.CubicadorNoDisponible, "caído", detalle)
        {
            Datos = new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true },
        };

    private void Responder(params clsVehiculoRecomendado[] filas) =>
        _cubicador.RecomendarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsMercanciaCubicador>>(), Arg.Any<CancellationToken>())
            .Returns(new clsRecomendacionCubicador(filas));

    private clsPlanificadorCamionesCubicador Planificador() => new(_logger, _cubicador);
}
