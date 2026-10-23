using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-032 (#197, ADR-022): el vehículo elegido como respaldo del cubicador en <see cref="clsPlanificadorCamionesCubicador"/>. Orden de
/// decisión por ruta: sin mercancías cubicables no se llama al cubicador y va el elegido (con <c>MERCANCIAS_NO_CUBICABLES</c> si es mixta);
/// con todas cubicables manda el cubicador si asigna (<c>VEHICULO_AJUSTADO_POR_CUBICADOR</c> si el elegido era otro) y, si no escoge o
/// falla, va el elegido validado TARDE contra Nexo (<c>VEHICULO_ELEGIDO_USADO</c> o, si no es de Nexo, sin vehículo con
/// <c>VEHICULO_ELEGIDO_INVALIDO</c>). Un rechazo de datos del cubicador sigue siendo error.
/// </summary>
public sealed class clsVehiculoElegidoPlanificadorTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private static readonly Guid _rutaId = Guid.NewGuid();
    private readonly IClsCubicador _cubicador = Substitute.For<IClsCubicador>();
    private readonly IClsValidadorCatalogosCaptura _catalogos = Substitute.For<IClsValidadorCatalogosCaptura>();
    private readonly clsLoggerFalso<clsPlanificadorCamionesCubicador> _logger = new();

    public clsVehiculoElegidoPlanificadorTests()
    {
        _cubicador.EstaConfigurado.Returns(true);
        _cubicador.RespaldoSiFalla.Returns(true);
        _cubicador.PresupuestoCaptura.Returns(TimeSpan.FromMinutes(1));
        // Por defecto el catálogo de Nexo conoce el vehículo y lo escribe en mayúsculas (como Canonica).
        _catalogos.ValidarLineasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var lineas = ci.Arg<IReadOnlyList<clsLineaSolicitudCosteo>>();
                return new clsValidacionCatalogoLineas([.. lineas.Select(_ => (IReadOnlyList<clsErrorCampo>)[])], null,
                    [.. lineas.Select(l => l with { TipoVehiculo = l.TipoVehiculo?.ToUpperInvariant(), Carroceria = l.Carroceria?.ToUpperInvariant() })]);
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Ruta de Cupo con todas sus mercancías cubicables (código, tres medidas y peso) y el vehículo elegido 2S2 - FURGON.</summary>
    private static clsLineaSolicitudCosteo Cubicable() => clsSolicitudCosteoMuestra.RutaConCodigos() with
    {
        ClaseViaje = "Cupo", ClaseContenedor = null, TipoVehiculo = "2s2", Carroceria = "furgon",
    };

    private static clsPosicionCarga SinMedidas(decimal peso) => new("Cajas sueltas", 1, 0m, 0m, 0m, peso);

    private clsPlanificadorCamionesCubicador Planificador() => new(_logger, _cubicador, catalogos: _catalogos);

    private void Responder(clsRecomendacionCubicador recomendacion) =>
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>()).Returns(recomendacion);

    private static clsRecomendacionCubicador Asignado(params clsVehiculoRecomendado[] vehiculos) =>
        new(Guid.NewGuid(), clsResultadosRecomendacion.Asignado, null, false, [], vehiculos, [], null);

    private static clsRecomendacionCubicador SinSeleccion() =>
        new(Guid.NewGuid(), clsResultadosRecomendacion.SinSeleccionAutomatica, clsMotivosSinSeleccion.SinCarroceriaCompatible, false, [], [], [], null);

    private static clsVehiculoRecomendado Vehiculo(string codigo, string carroceria, decimal peso) =>
        new(Guid.NewGuid(), codigo, carroceria, peso, 40m, 80m, 70m);

    // --- Clasificación: qué rutas van al cubicador ---

    [Fact]
    public void LaClasificacionDistingueTodasCubicablesNingunaMixtaYSinMercancias()
    {
        var cubicable = Cubicable();

        cubicable.Cubicabilidad.Should().Be(CubicabilidadRuta.Cubicable);
        (cubicable with { Posiciones = [] }).Cubicabilidad.Should().Be(CubicabilidadRuta.SinMercancias);
        (cubicable with { Posiciones = [SinMedidas(10m), SinMedidas(20m)] }).Cubicabilidad.Should().Be(CubicabilidadRuta.NoCubicable);
        (cubicable with { Posiciones = [cubicable.Posiciones[0], SinMedidas(20m)] }).Cubicabilidad.Should().Be(CubicabilidadRuta.Mixta);
    }

    [Fact]
    public async Task TodasCubicablesYElCubicadorAsignaOtroVehiculoMandaElCubicadorConElAvisoDelAjuste()
    {
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m), Vehiculo("2S2", "FURGON", 12_500m)));

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable(), Ct)).Valor;

        plan.Camiones.Select(c => c.TipoVehiculo).Should().Equal("3S3", "2S2");
        var aviso = plan.Avisos.Should().ContainSingle().Subject;
        aviso.Codigo.Should().Be(clsAvisosVehiculo.AjustadoPorCubicador);
        aviso.VehiculoElegido.Should().Be(new clsVehiculoAviso("2s2", "furgon"));
        aviso.VehiculosFinales.Should().Equal(new clsVehiculoAviso("3S3", "FURGON"), new clsVehiculoAviso("2S2", "FURGON"));
        await _catalogos.DidNotReceiveWithAnyArgs().ValidarLineasAsync(default, default!, Ct);
    }

    [Fact]
    public async Task SiElCubicadorAsignaElMismoVehiculoElegidoNoHayAviso()
    {
        Responder(Asignado(Vehiculo("2S2", "FURGON", 16_000m)));

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable(), Ct)).Valor;

        plan.Avisos.Should().BeNull("el elegido (2s2/furgon) es el mismo código escrito de otra forma");
    }

    [Fact]
    public async Task ConElCubicadorAsignadoUnElegidoInvalidoNoSeValidaNiImporta()
    {
        Responder(Asignado(Vehiculo("3S3", "FURGON", 16_000m)));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable() with { TipoVehiculo = "NO-EXISTE" }, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Camiones.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3");
        await _catalogos.DidNotReceiveWithAnyArgs().ValidarLineasAsync(default, default!, Ct);
    }

    [Fact]
    public async Task NingunaCubicableNoLlamaAlCubicadorYVaElVehiculoElegidoSinAviso()
    {
        var ruta = Cubicable() with { Posiciones = [SinMedidas(10_000m), SinMedidas(18_500.5m)], PesoKg = 28_500.5m, TipoVehiculo = "3S3", Carroceria = null };

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, ruta, Ct)).Valor;

        plan.Camiones.Should().Equal(new clsCamionPlaneado("3S3", null, 28_500.5m));
        plan.Cubicacion.Should().BeNull();
        plan.Avisos.Should().BeNull();
        await _cubicador.DidNotReceiveWithAnyArgs().RecomendarAsync(default!, Ct);
    }

    [Fact]
    public async Task SoloConPesoNoLlamaAlCubicadorYVaElVehiculoElegido()
    {
        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable() with { Posiciones = [], PesoKg = 9_000m }, Ct)).Valor;

        plan.Camiones.Should().Equal(new clsCamionPlaneado("2s2", "furgon", 9_000m));
        await _cubicador.DidNotReceiveWithAnyArgs().RecomendarAsync(default!, Ct);
    }

    [Fact]
    public async Task UnaRutaMixtaNoSeCubicaYVaElElegidoConElAvisoDeMercanciasNoCubicables()
    {
        var cubicable = Cubicable();
        var ruta = cubicable with { Posiciones = [cubicable.Posiciones[0], SinMedidas(500m)], PesoKg = null };

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, ruta, Ct)).Valor;

        plan.Camiones.Should().ContainSingle().Which.TipoVehiculo.Should().Be("2s2");
        plan.Avisos.Should().ContainSingle().Which.Codigo.Should().Be(clsAvisosVehiculo.MercanciasNoCubicables);
        await _cubicador.DidNotReceiveWithAnyArgs().RecomendarAsync(default!, Ct);
    }

    // --- Respaldo con el vehículo elegido (validación tardía) ---

    [Fact]
    public async Task SinSeleccionVaElVehiculoElegidoValidadoYEscritoComoEnNexoConSuAviso()
    {
        Responder(SinSeleccion());

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable(), Ct)).Valor;

        plan.Camiones.Should().Equal(new clsCamionPlaneado("2S2", "FURGON", clsPlanificadorCamionSinVehiculo.PesoTotal(Cubicable())));
        plan.Cubicacion!.Resultado.Should().Be(clsResultadosRecomendacion.SinSeleccionAutomatica, "la ruta guarda el resultado del cubicador");
        var aviso = plan.Avisos.Should().ContainSingle().Subject;
        aviso.Codigo.Should().Be(clsAvisosVehiculo.ElegidoUsado);
        aviso.Mensaje.Should().StartWith(clsPlanificadorCamionesCubicador.CausaSinSeleccion);
        await _catalogos.Received(1).ValidarLineasAsync(_usuario,
            Arg.Is<IReadOnlyList<clsLineaSolicitudCosteo>>(l => l.Count == 1 && l[0].TipoVehiculo == "2s2" && l[0].ClaseViaje == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinSeleccionConUnElegidoQueNoEsDeNexoLaRutaVaSinVehiculoConElAvisoYNoSePierde()
    {
        Responder(SinSeleccion());
        _catalogos.ValidarLineasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(new clsValidacionCatalogoLineas([[new clsErrorCampo("tipo_vehiculo", clsCodigosLineaSolicitud.CodigoNoCatalogo, "no")]], null));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable() with { TipoVehiculo = "NO-EXISTE" }, Ct);

        resultado.EsExitoso.Should().BeTrue("un elegido inválido no tumba la captura");
        resultado.Valor.Camiones.Should().ContainSingle().Which.Should().Match<clsCamionPlaneado>(c => c.TipoVehiculo == null && c.Carroceria == null);
        var aviso = resultado.Valor.Avisos.Should().ContainSingle().Subject;
        aviso.Codigo.Should().Be(clsAvisosVehiculo.ElegidoInvalido);
        aviso.VehiculoElegido!.TipoVehiculo.Should().Be("NO-EXISTE");
        aviso.VehiculosFinales.Should().Equal(new clsVehiculoAviso(null, null));
    }

    [Fact]
    public async Task SinSeleccionConSoloLaCarroceriaInvalidaVaElTipoSinCarroceria()
    {
        Responder(SinSeleccion());
        _catalogos.ValidarLineasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(new clsValidacionCatalogoLineas([[new clsErrorCampo("carroceria", clsCodigosLineaSolicitud.CodigoNoCatalogo, "no")]], null));

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable() with { TipoVehiculo = "2S2", Carroceria = "NAVE" }, Ct)).Valor;

        plan.Camiones.Should().ContainSingle().Which.Should().Match<clsCamionPlaneado>(c => c.TipoVehiculo == "2S2" && c.Carroceria == null);
        plan.Avisos.Should().ContainSingle().Which.Codigo.Should().Be(clsAvisosVehiculo.ElegidoInvalido);
    }

    [Fact]
    public async Task ConElCatalogoDeNexoCaidoElElegidoVaTalCualConElAvisoDeUso()
    {
        Responder(SinSeleccion());
        _catalogos.ValidarLineasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(new clsValidacionCatalogoLineas([[]],
                new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsValidadorCatalogosCaptura.MensajeSinCatalogo)));

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable(), Ct)).Valor;

        plan.Camiones.Should().ContainSingle().Which.TipoVehiculo.Should().Be("2s2");
        plan.Avisos.Should().ContainSingle().Which.Codigo.Should().Be(clsAvisosVehiculo.ElegidoUsado);
    }

    [Fact]
    public async Task SiElCubicadorSeCaeVaElVehiculoElegidoConElAvisoDeUso()
    {
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>()).Returns(
            new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "caído") { Datos = new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true } });

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable(), Ct)).Valor;

        plan.Camiones.Should().ContainSingle().Which.TipoVehiculo.Should().Be("2S2");
        var aviso = plan.Avisos.Should().ContainSingle().Subject;
        aviso.Codigo.Should().Be(clsAvisosVehiculo.ElegidoUsado);
        aviso.Mensaje.Should().StartWith(clsPlanificadorCamionesCubicador.CausaNoDisponible);
    }

    [Fact]
    public async Task SinSeleccionYSinElegidoElCamionQuedaSinVehiculoYSinAviso()
    {
        Responder(SinSeleccion());

        var plan = (await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable() with { TipoVehiculo = null, Carroceria = null }, Ct)).Valor;

        plan.Camiones.Should().ContainSingle().Which.TipoVehiculo.Should().BeNull();
        plan.Avisos.Should().BeNull();
        await _catalogos.DidNotReceiveWithAnyArgs().ValidarLineasAsync(default, default!, Ct);
    }

    [Theory]
    [InlineData(clsCodigosError.MercanciaDesconocida)]
    [InlineData(clsCodigosError.DimensionesInvalidas)]
    public async Task UnRechazoDeDatosDelCubicadorSigueSiendoErrorAunqueHayaVehiculoElegido(string codigo)
    {
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "dato malo"));

        var resultado = await Planificador().PlanificarAsync(_usuario, _rutaId, Cubicable(), Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
    }
}
