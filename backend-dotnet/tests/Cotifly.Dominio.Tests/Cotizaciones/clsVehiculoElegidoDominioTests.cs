using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-032 (#197, ADR-022) en el dominio: la clasificación de cubicabilidad de las mercancías y de la ruta (medidas parciales y código sin
/// medidas son error; sin medidas ni código, "no cubicable"), lo que viaja a Nexo (sin posiciones si alguna no tiene medidas), el vehículo
/// elegido guardado en la ruta, la regla de recubicación con el vehículo y el emparejamiento del camión del respaldo por ruta y peso cuando
/// la Mesa de Costeo le cambió el vehículo.
/// </summary>
public sealed class clsVehiculoElegidoDominioTests
{
    private static readonly clsEncabezadoSolicitudCosteo _encabezado = new() { Tipo = TipoSolicitud.Spot };

    private static clsPosicionCarga Cubicable(decimal peso = 1_000m) => new("Café", 1, 100m, 100m, 100m, peso, CodigoMercancia: "CAFE");

    private static clsPosicionCarga SinMedidas(decimal peso = 500m) => new("Bultos", 2, 0m, 0m, 0m, peso);

    private static clsLineaSolicitudCosteo Linea(string origen, string destino, decimal peso, string? vehiculo, params clsPosicionCarga[] mercancias) => new()
    {
        Origen = origen, Destino = destino, OrigenMunicipioId = 76109, DestinoMunicipioId = 11001, ClaseViaje = "Cupo", PesoKg = peso,
        TipoVehiculo = vehiculo, Posiciones = mercancias,
    };

    // --- Clasificación de la mercancía ---

    [Fact]
    public void UnaMercanciaSinNingunaMedidaNiCodigoEsNoCubicableYEsValida()
    {
        var mercancia = SinMedidas();

        (mercancia.EsNoCubicable, mercancia.EsCubicable, mercancia.PrimerError(1)).Should().Be((true, false, (clsErrorPosicion?)null));
    }

    [Fact]
    public void UnaMercanciaConCodigoTresMedidasYPesoEsCubicable()
    {
        Cubicable().EsCubicable.Should().BeTrue();
        (Cubicable() with { PesoKg = null }).EsCubicable.Should().BeFalse("sin peso por unidad el cubicador no la acepta");
    }

    [Theory]
    [InlineData(10, 0, 0, "ancho_cm")]
    [InlineData(0, 10, 10, "largo_cm")]
    [InlineData(10, 10, 0, "alto_cm")]
    public void LasMedidasParcialesSonUnDatoMalDigitadoEnLaPrimeraQueFalta(decimal largo, decimal ancho, decimal alto, string campo)
    {
        var error = new clsPosicionCarga("Caja", 1, largo, ancho, alto, 10m).PrimerError(2);

        error.Should().NotBeNull();
        error!.Campo.Should().Be(campo);
        error.Mensaje.Should().StartWith($"Mercancía 2: faltan medidas ({campo})");
    }

    [Fact]
    public void UnaMercanciaConCodigoYSinMedidasEsUnError()
    {
        var error = new clsPosicionCarga("Café", 1, 0m, 0m, 0m, 10m, CodigoMercancia: "CAFE").PrimerError(1);

        error!.Campo.Should().Be("largo_cm");
        error.Mensaje.Should().Contain("trae el tipo de mercancía pero no sus medidas");
    }

    [Fact]
    public void LaRutaConMercanciasNoCubicablesSigueValidandoElPeso()
    {
        // La regla de peso se mantiene: la suma de las mercancías (también las sin medidas) debe cuadrar con el peso de la ruta.
        var linea = Linea("A", "B", 5_000m, "3S3", SinMedidas(500m));

        linea.Errores().Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.PesoInconsistente);
        (linea with { PesoKg = 1_000m }).Errores().Should().BeEmpty();
    }

    // --- Ruta: lo que se exige y lo que viaja a Nexo ---

    [Fact]
    public void SinMercanciasCubicablesLaRutaExigeVehiculoSalvoEnContenedor()
    {
        Linea("A", "B", 1_000m, null).ExigeVehiculoElegido.Should().BeTrue();
        Linea("A", "B", 1_000m, null, SinMedidas(1_000m)).ExigeVehiculoElegido.Should().BeTrue();
        (Linea("A", "B", 1_000m, null) with { ClaseViaje = "Contenedor" }).ExigeVehiculoElegido.Should().BeFalse();
        Linea("A", "B", 1_000m, null, Cubicable()).ExigeVehiculoElegido.Should().BeFalse();
    }

    [Fact]
    public void LasMercanciasSinMedidasNoViajanANexoComoPosicionesYConEllasLaLineaVaSinPosiciones()
    {
        Linea("A", "B", 1_500m, "3S3", Cubicable(), SinMedidas()).PosicionesParaNexo.Should().BeEmpty();
        Linea("A", "B", 1_000m, "3S3", Cubicable()).PosicionesParaNexo.Should().HaveCount(1);
    }

    [Fact]
    public void LaLineaDeNexoDeUnaRutaConMercanciasSinMedidasLlevaSoloElPesoDeLaRuta()
    {
        var cotizacion = Capturar(new clsRutaCapturada(Linea("A", "B", 2_000m, "3S3", Cubicable(), SinMedidas()), 3, 0,
            [new clsCamionPlaneado("3S3", null, 2_000m)], []));

        var (_, linea) = cotizacion.LineasNexo().Should().ContainSingle().Subject;

        (linea.PesoKg, linea.TipoVehiculo, linea.Posiciones.Count).Should().Be((2_000m, "3S3", 0));
    }

    [Fact]
    public void LaMercanciaSinMedidasSeGuardaConLasMedidasEnNullYVuelveConCeros()
    {
        var ruta = Capturar(new clsRutaCapturada(Linea("A", "B", 1_000m, "3S3", SinMedidas()), 3, 0, [new clsCamionPlaneado("3S3", null, 1_000m)], []))
            .PrimeraRuta!;

        var mercancia = ruta.Mercancias.Should().ContainSingle().Subject;
        (mercancia.LargoCm, mercancia.AnchoCm, mercancia.AltoCm).Should().Be(((decimal?)null, (decimal?)null, (decimal?)null));
        mercancia.APosicion().Should().Be(SinMedidas() with { Descripcion = "Bultos" });
    }

    // --- Vehículo elegido guardado en la ruta ---

    [Fact]
    public void LaRutaGuardaElVehiculoElegidoAparteDelDeSusCamiones()
    {
        var ruta = Capturar(new clsRutaCapturada(Linea("A", "B", 1_000m, " 2S2 ", Cubicable()) with { Carroceria = "FURGON" }, 3, 0,
            [new clsCamionPlaneado("3S3", "FURGON", 1_000m, Guid.NewGuid())], [], Cubicacion: new clsCubicacionRuta(Guid.NewGuid(), "ASIGNADO", null, false)))
            .PrimeraRuta!;

        (ruta.TipoVehiculoElegido, ruta.CarroceriaElegida).Should().Be(("2S2", "FURGON"));
        ruta.ALineaConElegido().TipoVehiculo.Should().Be("2S2");
        ruta.ALinea().TipoVehiculo.Should().Be("3S3", "ALinea sigue con el vehículo del camión (lo que viaja)");
        ruta.UsaVehiculoElegido.Should().BeFalse("lo asignó el cubicador");
    }

    [Fact]
    public void LosAvisosDelVehiculoSeGuardanConLasAdvertenciasDelCubicadorYReemplazanLosAnteriores()
    {
        var advertencia = new clsAdvertenciaCubicador("REFRIGERADO", "refrigerado = FURGON");
        var aviso = clsAvisosVehiculo.Usado(new clsVehiculoAviso("3S3", null), "El cubicador no seleccionó vehículo");
        var ruta = Capturar(new clsRutaCapturada(Linea("A", "B", 1_000m, "3S3", Cubicable()), 3, 0, [new clsCamionPlaneado("3S3", null, 1_000m)], [],
            Cubicacion: new clsCubicacionRuta(Guid.NewGuid(), "SIN_SELECCION_AUTOMATICA", null, false, new clsAvisosCubicacion([], [advertencia], null)))
        {
            AvisosVehiculo = [aviso],
        }).PrimeraRuta!;

        ruta.AvisosCubicacion!.Advertencias.Select(a => a.Codigo).Should().Equal("REFRIGERADO", clsAvisosVehiculo.ElegidoUsado);
        ruta.UsaVehiculoElegido.Should().BeTrue();

        ruta.ReemplazarCamiones([new clsCamionPlaneado("2S2", null, 1_000m)], null, [clsAvisosVehiculo.Mixta(new clsVehiculoAviso("2S2", null))]);

        ruta.AvisosCubicacion!.Advertencias.Select(a => a.Codigo).Should().Equal(clsAvisosVehiculo.MercanciasNoCubicables);
    }

    [Fact]
    public void UnaRutaAnteriorSinElegidoTomaElVehiculoDeSuCamionDelRespaldo()
    {
        var ruta = Capturar(new clsRutaCapturada(Linea("A", "B", 1_000m, null), 3, 0, [new clsCamionPlaneado("3S3", "FURGON", 1_000m)], [])).PrimeraRuta!;

        ruta.VehiculoElegido.Should().Be(("3S3", "FURGON"));
    }

    // --- Recubicación con el vehículo ---

    [Fact]
    public void CambiarSoloElVehiculoExigeReplanificarSoloSiLosCamionesDependenDelElegido()
    {
        var anterior = Linea("A", "B", 1_000m, "3S3");

        clsReglaRecubicacion.Exige(anterior, anterior with { TipoVehiculo = "2S2" }).Should().BeFalse("la regla del cubicador no mira el vehículo");
        clsReglaRecubicacion.Exige(anterior, anterior with { TipoVehiculo = "2S2" }, vehiculoCuenta: true).Should().BeTrue();
        clsReglaRecubicacion.Exige(anterior, anterior with { TipoVehiculo = "3s3" }, vehiculoCuenta: true).Should().BeFalse("el mismo código");
        clsReglaRecubicacion.Exige(anterior, anterior with { Carroceria = "FURGON" }, vehiculoCuenta: true).Should().BeTrue();
    }

    // --- Emparejamiento del camión del respaldo (regla 6) ---

    [Fact]
    public void VariasRutasDelRespaldoSeAsocianPorRutaYPesoAunqueLaMesaCambieElVehiculoDeUnaAntesDelPrimerPolling()
    {
        var cotizacion = Capturar(
            new clsRutaCapturada(Linea("Buenaventura", "Bogotá", 10_000m, "3S3"), 3, 0, [new clsCamionPlaneado("3S3", null, 10_000m)], []),
            new clsRutaCapturada(Linea("Buenaventura", "Bogotá", 8_000m, "2S2"), 3, 1, [new clsCamionPlaneado("2S2", null, 8_000m)], []),
            new clsRutaCapturada(Linea("Cali", "Medellín", 5_000m, "C2"), 3, 2, [new clsCamionPlaneado("C2", "ESTACAS", 5_000m)], []));
        var camiones = Camiones(cotizacion);
        clsLineaSolicitudNexo[] nexo =
        [
            new("N1", "Buenaventura → Bogotá", null, 10_000m, "3S3", null),
            // La Mesa de Costeo cambió el 2S2 por un 3S3 y la carrocería de la ruta 3 antes del primer polling.
            new("N2", "Buenaventura → Bogotá", null, 8_000m, "3S3", null),
            new("N3", "Cali → Medellín", null, 5_000m, "C2", "FURGON"),
        ];

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones, nexo);

        pares.Should().HaveCount(3, "cada camión del respaldo tiene una ruta y un peso únicos en la cotización");
        pares[cotizacion.RutasOrdenadas[1].Vehiculos[0].Id].Should().Be("N2");
        pares[cotizacion.RutasOrdenadas[2].Vehiculos[0].Id].Should().Be("N3");
    }

    [Fact]
    public void SiLaRutaYElPesoNoSonUnicosNoSeAsociaPorEllosYQuedaSinAsociar()
    {
        var cotizacion = Capturar(
            new clsRutaCapturada(Linea("Buenaventura", "Bogotá", 8_000m, "3S3"), 3, 0, [new clsCamionPlaneado("3S3", null, 8_000m)], []),
            new clsRutaCapturada(Linea("Buenaventura", "Bogotá", 8_000m, "2S2"), 3, 1, [new clsCamionPlaneado("2S2", null, 8_000m)], []));
        // La Mesa cambió el vehículo de las dos líneas: ninguna cuadra por contenido y la ruta y el peso no las distinguen.
        clsLineaSolicitudNexo[] nexo =
        [
            new("N1", "Buenaventura → Bogotá", null, 8_000m, "C2", null),
            new("N2", "Buenaventura → Bogotá", null, 8_000m, "C3", null),
        ];

        var detalle = clsEmparejadorLineasNexo.EmparejarConDetalle(Camiones(cotizacion), nexo);

        detalle.Pares.Should().BeEmpty("dos camiones y dos líneas con la misma ruta y peso: asociarlos sería adivinar");
        detalle.CamionesSinAsociar.Should().HaveCount(2);
    }

    [Fact]
    public void UnaLineaDeUnCamionDelCubicadorNoSeLaQuitaElRespaldoPorRutaYPeso()
    {
        var recomendacion = Guid.NewGuid();
        var cotizacion = Capturar(
            new clsRutaCapturada(Linea("Buenaventura", "Bogotá", 8_000m, "3S3", Cubicable(8_000m)), 3, 0,
                [new clsCamionPlaneado("3S3", "FURGON", 8_000m, recomendacion)], [], Cubicacion: new clsCubicacionRuta(Guid.NewGuid(), "ASIGNADO", null, false)),
            new clsRutaCapturada(Linea("Buenaventura", "Bogotá", 8_000m, "2S2"), 3, 1, [new clsCamionPlaneado("2S2", null, 8_000m)], []));
        // Nexo no devuelve la recomendación y la Mesa cambió el vehículo de la línea del cubicador: ninguna línea es segura para el respaldo.
        clsLineaSolicitudNexo[] nexo = [new("N1", "Buenaventura → Bogotá", null, 8_000m, "C2", null)];

        clsEmparejadorLineasNexo.Emparejar(Camiones(cotizacion), nexo).Should().BeEmpty();
    }

    private static clsCotizacionSolicitud Capturar(params clsRutaCapturada[] rutas)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-HU032-1" }).Valor;
        cotizacion.RegistrarCaptura(_encabezado, rutas);
        return cotizacion;
    }

    private static IReadOnlyList<clsCamionParaEmparejar> Camiones(clsCotizacionSolicitud cotizacion) =>
        [.. cotizacion.RutasOrdenadas.SelectMany(r => r.VehiculosOrdenados.Select(v => clsCamionParaEmparejar.Desde(r, v)))];
}
