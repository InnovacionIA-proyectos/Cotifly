using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-030.1 (#184): peso con tara de una ruta de contenedor. Solo con <c>clase_viaje = Contenedor</c> y nunca menor que el peso de carga;
/// viaja a Nexo como el peso de las líneas de la ruta (la tara repartida por partes iguales entre sus camiones, el resto del redondeo en
/// el último) y, entonces, sin posiciones (Nexo valida <c>PESO_INCONSISTENTE</c> al 1 %, NEXO#308). Sin tara, todo como antes.
/// </summary>
public sealed class clsPesoConTaraTests
{
    private static clsLineaSolicitudCosteo Linea(string claseViaje = "Contenedor", decimal? pesoKg = 28_000m, decimal? pesoConTaraKg = null) => new()
    {
        Origen = "Buenaventura",
        Destino = "Bogotá, D.C.",
        OrigenMunicipioId = 76109,
        DestinoMunicipioId = 11001,
        Tipologia = "Contenedor",
        ClaseViaje = claseViaje,
        ClaseContenedor = claseViaje == "Contenedor" ? "CNT40.HC" : null,
        PesoKg = pesoKg,
        PesoConTaraKg = pesoConTaraKg,
        Posiciones = [new clsPosicionCarga("Estiba de café", 20, 120, 100, 150, 1_400m, CodigoMercancia: "CAFE_EN_ESTIBAS")],
    };

    private static clsLineaSolicitud Ruta(clsLineaSolicitudCosteo linea, params decimal?[] camiones)
    {
        var ruta = clsLineaSolicitud.Desde(Guid.NewGuid(), 1, 0, 1, linea);
        foreach (var peso in camiones)
        {
            ruta.AgregarVehiculo("3S3", "FURGON", peso, Guid.NewGuid());
        }

        return ruta;
    }

    // ------------------------------------------------------------------ Validaciones

    [Theory]
    [InlineData(28_000)]
    [InlineData(31_800.55)]
    [InlineData(999_999.99)]
    public void EnContenedorUnPesoConTaraNoMenorQueLaCargaYDentroDelTopeEsValido(double conTara) =>
        Linea(pesoConTaraKg: (decimal)conTara).Errores().Should().BeEmpty();

    [Fact]
    public void ConOtraClaseDeViajeElPesoConTaraEsCampoNoAplica()
    {
        var errores = Linea(claseViaje: "Cupo", pesoConTaraKg: 30_000m).Errores();

        errores.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Campo = "peso_con_tara_kg",
            Codigo = clsCodigosLineaSolicitud.CampoNoAplica,
        });
        errores[0].Mensaje.Should().Contain("Contenedor");
        Linea(claseViaje: "Cupo").Errores().Should().BeEmpty("sin tara las demás clases no cambian");
    }

    [Fact]
    public void UnPesoConTaraMenorQueLaCargaEsPesoConTaraMenorQueCarga()
    {
        var error = Linea(pesoConTaraKg: 27_999.99m).Errores().Should().ContainSingle().Subject;

        (error.Campo, error.Codigo).Should().Be(("peso_con_tara_kg", clsCodigosLineaSolicitud.PesoConTaraMenorQueCarga));
        error.Mensaje.Should().Contain("27999").And.Contain("28000");
        Linea(pesoConTaraKg: 0m).Errores().Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.PesoConTaraMenorQueCarga,
            "0 kg nunca es un peso con tara (la base además exige > 0)");
    }

    [Fact]
    public void SinPesoKgLaCargaEsLaSumaDeLasMercancias()
    {
        var linea = Linea(pesoKg: null, pesoConTaraKg: 27_000m);

        linea.PesoCarga.Should().Be(28_000m, "20 estibas de 1400 kg");
        linea.Errores().Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.PesoConTaraMenorQueCarga);
        (linea with { PesoConTaraKg = 31_600m }).Errores().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    [InlineData(30_000.001)]
    public void UnPesoConTaraFueraDeRangoOConMasDeDosDecimalesEsValorFueraDeRango(double conTara) =>
        Linea(pesoConTaraKg: (decimal)conTara).Errores()
            .Should().Contain(e => e.Campo == "peso_con_tara_kg" && e.Codigo == clsCodigosLineaSolicitud.ValorFueraDeRango);

    [Fact]
    public void ElFormularioDeUnaLineaAplicaLasMismasReglas()
    {
        var encabezado = new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot };

        clsSolicitudCosteo.Desde(encabezado, Linea(pesoConTaraKg: 30_000m)).Validar().Should().BeNull();
        clsSolicitudCosteo.Desde(encabezado, Linea(pesoConTaraKg: 30_000m)).ALinea().PesoConTaraKg.Should().Be(30_000m);
        clsSolicitudCosteo.Desde(encabezado, Linea(claseViaje: "Cupo", pesoConTaraKg: 30_000m)).Validar()!.Mensaje.Should().Contain("peso_con_tara_kg");
        clsSolicitudCosteo.Desde(encabezado, Linea(pesoConTaraKg: 1_000m)).Validar()!.Mensaje.Should().Contain("peso total con tara");
    }

    // ------------------------------------------------------------------ Lo que viaja a Nexo

    [Fact]
    public void ConUnCamionLaLineaLlevaElPesoConTaraYNoLlevaPosiciones()
    {
        var ruta = Ruta(Linea(pesoConTaraKg: 31_800m), 28_000m);

        var linea = ruta.ALineaNexo(ruta.Vehiculos[0]);

        linea.PesoKg.Should().Be(31_800m);
        linea.Posiciones.Should().BeEmpty("Nexo validaría PESO_INCONSISTENTE contra la suma de las posiciones (28000 kg)");
        linea.PesoConTaraKg.Should().BeNull("el peso de la línea ya es el peso con tara");
        ruta.PesoConTaraCamion(ruta.Vehiculos[0]).Should().Be(31_800m);
        ruta.ALinea().PesoConTaraKg.Should().Be(31_800m, "la ruta guardada conserva lo que escribió el comercial");
        ruta.ALinea().PesoKg.Should().Be(28_000m, "el peso de carga no cambia");
    }

    [Fact]
    public void SinTaraConUnCamionTodoSigueComoAntes()
    {
        var ruta = Ruta(Linea(), 27_500m);

        var linea = ruta.ALineaNexo(ruta.Vehiculos[0]);

        linea.PesoKg.Should().Be(28_000m, "el peso declarado de la ruta, que cuadra con las posiciones");
        linea.Posiciones.Should().ContainSingle();
        ruta.LlevaTara.Should().BeFalse();
        ruta.PesoConTaraCamion(ruta.Vehiculos[0]).Should().BeNull();
    }

    [Fact]
    public void ConVariosCamionesLaTaraSeRepartePorPartesIgualesYElUltimoSeQuedaConElResto()
    {
        // Carga 30000 kg en tres camiones; con tara 36666.67 kg: 6666.67 kg de tara, 2222.22 por camión y 0.01 de resto al último.
        var ruta = Ruta(Linea(pesoKg: 30_000m, pesoConTaraKg: 36_666.67m) with { Posiciones = [] }, 10_000.01m, 10_000m, 9_999.99m);

        var pesos = ruta.VehiculosOrdenados.Select(v => ruta.ALineaNexo(v).PesoKg!.Value).ToList();

        pesos.Should().Equal(12_222.23m, 12_222.22m, 12_222.22m);
        pesos.Sum().Should().Be(36_666.67m, "la suma de las líneas es exactamente el peso con tara de la ruta");
        ruta.VehiculosOrdenados.Should().OnlyContain(v => ruta.ALineaNexo(v).Posiciones.Count == 0);
        ruta.VehiculosOrdenados.Select(ruta.PesoConTaraCamion).Should().Equal(12_222.23m, 12_222.22m, 12_222.22m);
    }

    [Fact]
    public void SinTaraConVariosCamionesCadaLineaLlevaElPesoDeSuCamion()
    {
        var ruta = Ruta(Linea(), 16_000m, 12_000m);

        ruta.VehiculosOrdenados.Select(v => ruta.ALineaNexo(v).PesoKg).Should().Equal(16_000m, 12_000m);
    }

    [Fact]
    public void UnaRutaGuardadaConTaraQueYaNoEsContenedorNoLaMandaANexo()
    {
        // La captura no lo permite; si una ruta guardada llega así (dato anterior), la tara no viaja en lugar de mandar un peso falso.
        var ruta = Ruta(Linea(claseViaje: "Cupo", pesoConTaraKg: 35_000m), 28_000m);

        ruta.LlevaTara.Should().BeFalse();
        var linea = ruta.ALineaNexo(ruta.Vehiculos[0]);
        linea.PesoKg.Should().Be(28_000m);
        linea.Posiciones.Should().ContainSingle();
    }

    [Fact]
    public void ElEmparejadorComparaConElPesoConTaraQueViajo()
    {
        var ruta = Ruta(Linea(pesoConTaraKg: 36_000m), 16_000m, 12_000m);

        ruta.VehiculosOrdenados.Select(v => clsCamionParaEmparejar.Desde(ruta, v).PesoKg).Should().Equal(20_000m, 16_000m);
    }

    [Fact]
    public void LaEdicionCambiaOQuitaLaTaraSinTocarLosCamiones()
    {
        var ruta = Ruta(Linea(), 28_000m);
        var camion = ruta.Vehiculos[0];

        ruta.AplicarEdicion(1, new clsRutaCapturada(Linea(pesoConTaraKg: 32_000m), 1, 0, [], []), null, replanificada: false);

        ruta.PesoConTaraKg.Should().Be(32_000m);
        ruta.Vehiculos.Should().ContainSingle().Which.Should().BeSameAs(camion, "la tara no vuelve a cubicar");
        ruta.ALineaNexo(camion).PesoKg.Should().Be(32_000m);

        ruta.AplicarEdicion(1, new clsRutaCapturada(Linea(), 1, 0, [], []), null, replanificada: false);
        ruta.PesoConTaraKg.Should().BeNull();
        ruta.ALineaNexo(camion).Posiciones.Should().ContainSingle();
    }

    [Fact]
    public void LosCambiosDeCargaDeUnaReSolicitudAplicanOQuitanLaTara()
    {
        var guardada = Linea(pesoConTaraKg: 31_000m);

        new clsCambiosCarga { Presentes = new HashSet<string> { "peso_con_tara_kg" }, PesoConTaraKg = 33_000m }.Aplicar(guardada)
            .PesoConTaraKg.Should().Be(33_000m);
        new clsCambiosCarga { Presentes = new HashSet<string> { "peso_con_tara_kg" } }.Aplicar(guardada).PesoConTaraKg.Should().BeNull();
        new clsCambiosCarga { Presentes = new HashSet<string> { "peso_kg" }, PesoKg = 28_000m }.Aplicar(guardada)
            .PesoConTaraKg.Should().Be(31_000m, "un campo que no viene se conserva");
        clsCambiosCarga.Campos.Should().Contain("peso_con_tara_kg");
    }
}
