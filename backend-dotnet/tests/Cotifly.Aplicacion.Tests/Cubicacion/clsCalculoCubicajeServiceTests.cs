using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Cubicacion.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cubicacion;

/// <summary>
/// <see cref="clsCalculoCubicajeService"/> ("Calcular cubicaje", HU-023.2, #157): la petición de prueba (<c>PRUEBA-&lt;guid&gt;</c>), la
/// lista de vehículos con los nombres del catálogo, los cuatro motivos de "sin selección", la condicionada, la resolución de la
/// descripción y los errores (sin cubicador, caída, rechazo y sin coincidencia).
/// </summary>
public sealed class clsCalculoCubicajeServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsCubicador _cubicador = Substitute.For<IClsCubicador>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly clsLoggerFalso<clsCalculoCubicajeService> _logger = new();

    public clsCalculoCubicajeServiceTests()
    {
        _cubicador.EstaConfigurado.Returns(true);
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito(
        [
            new clsTipoMercanciaCubicador("CAFE_EN_ESTIBAS", "Café en estibas", "GENERAL", true, false, null, true),
            new clsTipoMercanciaCubicador("QUIMICOS", "Tambor de químicos", "PELIGROSA", true, true, "Requiere aprobación", true),
        ]));
        _cubicador.ListarTiposVehiculoAsync(Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsTipoVehiculoCubicador>>.Exito(
            [new clsTipoVehiculoCubicador("3S3", "Tractocamión 3S3", ["FURGON"], true)]));
        _cubicador.ListarTiposCarroceriaAsync(Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsTipoCarroceriaCubicador>>.Exito(
            [new clsTipoCarroceriaCubicador("FURGON", "Furgón", true)]));
        _usuarios.ObtenerPorIdAsync(_usuario, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("ana", "Ana", "ana@x.co", null, DateTimeOffset.UtcNow, 987654).Valor);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsCalculoCubicajeService Servicio() => new(_cubicador, _usuarios, _logger);

    private static clsItemCubicacion Item(string? codigo = "CAFE_EN_ESTIBAS", string? descripcion = null) =>
        new(codigo, descripcion, 10, 120m, 100m, 150m, 800m);

    private static clsRecomendacionCubicador Respuesta(
        string resultado, string? motivo, bool condicionada = false, params clsVehiculoRecomendado[] vehiculos) =>
        new(Guid.NewGuid(), resultado, motivo, condicionada,
            condicionada ? [new clsMercanciaCondicionada("QUIMICOS", "Requiere aprobación")] : [],
            vehiculos,
            [new clsAdvertenciaCubicador("REFRIGERADO", "Confirmar el equipo de frío.")],
            motivo == clsMotivosSinSeleccion.Extradimensionado ? new clsExtradimensionCubicador(true, ["ALTO"], "Supera el alto.") : null);

    private void Responder(clsResultado<clsRecomendacionCubicador> respuesta) =>
        _cubicador.RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>()).Returns(respuesta);

    [Fact]
    public async Task FlotaMixtaDevuelveLaListaDeVehiculosConLosNombresDelCatalogoYSinCatalogoLosCodigos()
    {
        var id1 = Guid.NewGuid();
        Responder(Respuesta(clsResultadosRecomendacion.Asignado, null, false,
            new clsVehiculoRecomendado(id1, "3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m),
            new clsVehiculoRecomendado(Guid.NewGuid(), "C2", "ESTACAS", 4_500.5m, 18.25m, 60m, 75.01m, "Sencillo C2 (cubicador)"),
            new clsVehiculoRecomendado(Guid.NewGuid(), "2S2", "CAMA_BAJA", 1_000m, 5m, 10m, 20m)));

        var resultado = (await Servicio().CalcularAsync(_usuario, [Item()], "Contenedor", Ct)).Valor;

        resultado.Resultado.Should().Be("ASIGNADO");
        resultado.MotivoSinSeleccion.Should().BeNull();
        resultado.Condicionada.Should().BeFalse();
        resultado.Vehiculos.Should().Equal(
            new clsVehiculoCubicacion("3S3", "Tractocamión 3S3", "FURGON", "Furgón", 16_000m, 62.5m, 81.25m, 47.06m),
            new clsVehiculoCubicacion("C2", "Sencillo C2 (cubicador)", "ESTACAS", "ESTACAS", 4_500.5m, 18.25m, 60m, 75.01m),
            new clsVehiculoCubicacion("2S2", "2S2", "CAMA_BAJA", "CAMA_BAJA", 1_000m, 5m, 10m, 20m));
        resultado.Advertencias.Should().ContainSingle().Which.Codigo.Should().Be("REFRIGERADO");
        resultado.Mercancias.Should().Equal(new clsMercanciaCubicada("CAFE_EN_ESTIBAS", null, null, false));
    }

    [Fact]
    public async Task SinLaBanderaNoTraeLaSugerenciaLegada()
    {
        Responder(Respuesta(clsResultadosRecomendacion.Asignado, null, false,
            new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m)));

        (await Servicio().CalcularAsync(_usuario, [Item()], null, Ct)).Valor.Sugerencia.Should().BeNull();
    }

    [Fact]
    public async Task ConLaBanderaDelPlanBTraeLaSugerenciaLegadaDelPrimerVehiculo()
    {
        Responder(Respuesta(clsResultadosRecomendacion.Asignado, null, false,
            new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m),
            new clsVehiculoRecomendado(Guid.NewGuid(), "C2", "ESTACAS", 4_500m, 18m, 60m, 75m)));
        var servicio = new clsCalculoCubicajeService(_cubicador, _usuarios, _logger,
            Microsoft.Extensions.Options.Options.Create(new clsOpcionesCubicacion { IncluirSugerenciaLegada = true }));

        var resultado = (await servicio.CalcularAsync(_usuario, [Item()], null, Ct)).Valor;

        resultado.Sugerencia.Should().Be(new clsSugerenciaVehiculoLegada("3S3", "Tractocamión 3S3", "cubicador", 0.8125m, "3S3", "FURGON", null, null));
        resultado.Vehiculos.Should().HaveCount(2, "la lista sigue completa");

        Responder(Respuesta(clsResultadosRecomendacion.SinSeleccionAutomatica, clsMotivosSinSeleccion.SuperaKMax));
        (await servicio.CalcularAsync(_usuario, [Item()], null, Ct)).Valor.Sugerencia.Should().BeNull("sin vehículos no hay sugerencia");
    }

    [Fact]
    public async Task ElParecidoMinimoDeLaDescripcionEsConfigurable()
    {
        Responder(Respuesta(clsResultadosRecomendacion.SinSeleccionAutomatica, clsMotivosSinSeleccion.SuperaKMax));
        var estricto = new clsCalculoCubicajeService(_cubicador, _usuarios, _logger,
            Microsoft.Extensions.Options.Options.Create(new clsOpcionesCubicacion { ParecidoMinimoMercancia = 0.99 }));

        (await estricto.CalcularAsync(_usuario, [Item(null, "café")], null, Ct)).Error.Codigo.Should().Be(clsCodigosError.MercanciaDesconocida);
        (await Servicio().CalcularAsync(_usuario, [Item(null, "café")], null, Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task SinElCatalogoDeVehiculosMuestraLosCodigos()
    {
        _cubicador.ListarTiposVehiculoAsync(Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "P1 sin publicar"));
        Responder(Respuesta(clsResultadosRecomendacion.Asignado, null, false,
            new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m)));

        var vehiculo = (await Servicio().CalcularAsync(_usuario, [Item()], null, Ct)).Valor.Vehiculos.Should().ContainSingle().Subject;

        vehiculo.NombreVehiculo.Should().Be("3S3");
        vehiculo.NombreCarroceria.Should().Be("Furgón");
    }

    [Fact]
    public async Task MandaUnIdDePruebaNuevoElOrigenLaClaseDeViajeElUsuarioExternoYLasMercancias()
    {
        var peticiones = new List<clsPeticionRecomendacion>();
        _cubicador.RecomendarAsync(Arg.Do<clsPeticionRecomendacion>(peticiones.Add), Arg.Any<CancellationToken>())
            .Returns(Respuesta(clsResultadosRecomendacion.SinSeleccionAutomatica, clsMotivosSinSeleccion.SuperaKMax));

        await Servicio().CalcularAsync(_usuario, [Item(" CAFE_EN_ESTIBAS ")], " Contenedor ", Ct);
        await Servicio().CalcularAsync(_usuario, [Item()], "  ", Ct);

        peticiones.Should().HaveCount(2);
        foreach (var id in peticiones.Select(p => p.IdLineaSolicitud))
        {
            id.Should().StartWith(clsCalculoCubicajeService.PrefijoPrueba);
            Guid.TryParseExact(id[clsCalculoCubicajeService.PrefijoPrueba.Length..], "D", out _).Should().BeTrue(id);
        }

        peticiones[0].IdLineaSolicitud.Should().NotBe(peticiones[1].IdLineaSolicitud, "cada cálculo es una corrida de prueba nueva");
        peticiones[0].ClaseViajeSeleccionada.Should().Be("Contenedor");
        peticiones[1].ClaseViajeSeleccionada.Should().BeNull();
        peticiones[0].UsuarioId.Should().Be("987654");
        peticiones[0].Mercancias.Should().Equal(new clsMercanciaCubicador("CAFE_EN_ESTIBAS", 10, 120m, 100m, 150m, 800m));
        await _cubicador.DidNotReceive().ListarTiposMercanciaAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(clsMotivosSinSeleccion.Extradimensionado)]
    [InlineData(clsMotivosSinSeleccion.SinCandidatosDimensionales)]
    [InlineData(clsMotivosSinSeleccion.SuperaKMax)]
    [InlineData(clsMotivosSinSeleccion.SinCarroceriaCompatible)]
    public async Task CadaMotivoDeSinSeleccionLlegaSinVehiculos(string motivo)
    {
        Responder(Respuesta(clsResultadosRecomendacion.SinSeleccionAutomatica, motivo));

        var resultado = (await Servicio().CalcularAsync(_usuario, [Item()], null, Ct)).Valor;

        resultado.Resultado.Should().Be("SIN_SELECCION_AUTOMATICA");
        resultado.MotivoSinSeleccion.Should().Be(motivo);
        resultado.Vehiculos.Should().BeEmpty();
        if (motivo == clsMotivosSinSeleccion.Extradimensionado)
        {
            resultado.Extradimension.Should().NotBeNull();
            resultado.Extradimension!.Extradimensionado.Should().BeTrue();
            resultado.Extradimension.Dimensiones.Should().Equal("ALTO");
            resultado.Extradimension.Detalle.Should().Be("Supera el alto.");
        }

        await _cubicador.DidNotReceive().ListarTiposVehiculoAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaCondicionadaLlegaConSusMercanciasYSusVehiculos()
    {
        Responder(Respuesta(clsResultadosRecomendacion.Asignado, null, true,
            new clsVehiculoRecomendado(Guid.NewGuid(), "3S3", "FURGON", 9_000m, 30m, 55.5m, 40m)));

        var resultado = (await Servicio().CalcularAsync(_usuario, [Item("QUIMICOS")], null, Ct)).Valor;

        resultado.Condicionada.Should().BeTrue();
        resultado.MercanciasCondicionadas.Should().Equal(new clsMercanciaCondicionada("QUIMICOS", "Requiere aprobación"));
        resultado.Vehiculos.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Café", "CAFE_EN_ESTIBAS", "Café en estibas")]
    [InlineData("CAFE EN ESTIBAS", "CAFE_EN_ESTIBAS", "Café en estibas")]
    [InlineData("quimicos", "QUIMICOS", "Tambor de químicos")]
    public async Task LaDescripcionSeResuelveAlCodigoMasParecido(string descripcion, string codigo, string nombre)
    {
        Responder(Respuesta(clsResultadosRecomendacion.SinSeleccionAutomatica, clsMotivosSinSeleccion.SuperaKMax));

        var resultado = (await Servicio().CalcularAsync(_usuario, [Item(null, descripcion), Item("QUIMICOS", "lo que sea"), Item(null, descripcion)], null, Ct)).Valor;

        resultado.Mercancias.Should().Equal(
            new clsMercanciaCubicada(codigo, nombre, descripcion, true),
            new clsMercanciaCubicada("QUIMICOS", null, "lo que sea", false),
            new clsMercanciaCubicada(codigo, nombre, descripcion, true));
        await _cubicador.Received(1).RecomendarAsync(
            Arg.Is<clsPeticionRecomendacion>(p => p.Mercancias.Select(m => m.CodigoMercancia).SequenceEqual(new[] { codigo, "QUIMICOS", codigo })),
            Arg.Any<CancellationToken>());
        await _cubicador.Received(1).ListarTiposMercanciaAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaDescripcionSinCoincidenciaEsMercanciaDesconocidaYNoLlamaAlCubicador()
    {
        var resultado = await Servicio().CalcularAsync(_usuario, [Item(), Item(null, "Electrodomésticos")], null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MercanciaDesconocida);
        resultado.Error.Mensaje.Should().Contain("«Electrodomésticos»").And.Contain("ítem 2");
        resultado.Error.Datos.Should().Contain("item", 1);
        await _cubicador.DidNotReceive().RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnItemSinCodigoNiDescripcionEsMercanciaDesconocida()
    {
        var resultado = await Servicio().CalcularAsync(_usuario, [Item(null, "  ")], null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MercanciaDesconocida);
        resultado.Error.Mensaje.Should().Contain("ítem 1");
    }

    [Fact]
    public async Task SinCubicadorConfiguradoResponde503SinLlamar()
    {
        _cubicador.EstaConfigurado.Returns(false);

        var resultado = await Servicio().CalcularAsync(_usuario, [Item()], null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Mensaje.Should().Be(clsCalculoCubicajeService.MensajeNoConfigurado);
        await _cubicador.DidNotReceive().RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(clsCodigosError.CubicadorNoDisponible)]
    [InlineData("OTRO_RECHAZO_NO_PREVISTO")]
    public async Task UnCubicadorCaidoEsNoDisponibleSinDetalleTecnico(string codigo)
    {
        Responder(new clsErrorDominio(codigo, "caído", "HTTP 503 en /recomendaciones"));

        var resultado = await Servicio().CalcularAsync(_usuario, [Item()], null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Mensaje.Should().Be(clsCalculoCubicajeService.MensajeNoDisponible);
        resultado.Error.Detalle.Should().BeNull("el detalle técnico solo va al log");
    }

    [Fact]
    public async Task UnCatalogoCaidoAlResolverLaDescripcionEsNoDisponible()
    {
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "caído", "tiempo agotado"));

        var resultado = await Servicio().CalcularAsync(_usuario, [Item(null, "café")], null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        await _cubicador.DidNotReceive().RecomendarAsync(Arg.Any<clsPeticionRecomendacion>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(clsCodigosError.MercanciaDesconocida)]
    [InlineData(clsCodigosError.MercanciaNoCubicable)]
    [InlineData(clsCodigosError.DimensionesInvalidas)]
    [InlineData(clsCodigosError.MercanciasRequeridas)]
    [InlineData(clsCodigosError.CubicadorNoAutorizado)]
    [InlineData(clsCodigosError.CubicadorRespuestaInvalida)]
    public async Task LosRechazosDelCubicadorLleganConSuCodigoYSinDetalle(string codigo)
    {
        Responder(new clsErrorDominio(codigo, "Mensaje para el comercial.", "detalle interno"));

        var resultado = await Servicio().CalcularAsync(_usuario, [Item("DESCONOCIDO")], null, Ct);

        resultado.Error.Should().Be(new clsErrorDominio(codigo, "Mensaje para el comercial."));
    }
}
