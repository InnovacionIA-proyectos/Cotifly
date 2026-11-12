using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Lectura del costeo de Nexo camión por camión: N camiones por ruta con las líneas de Nexo en cualquier orden, empates
/// de camiones idénticos, el vehículo que pone la Mesa, el camión descartado que no se lee, los conexos que vuelven a su ruta y la nota
/// del envío para la Mesa.
/// </summary>
public sealed class clsCosteoPorCamionTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 8, 16, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new();

    [Fact]
    public void CadaCamionRecibeElFleteDeSuLineaAunqueNexoLasDevuelvaEnOtroOrden()
    {
        var lineas = new[] { ("3S3", 10_000m, "N-a", 1_000_000m), ("2S2", 8_000m, "N-b", 600_000m), ("2", 4_000m, "N-c", 400_000m) };

        foreach (var orden in new[] { new[] { 2, 0, 1 }, [1, 2, 0], [0, 1, 2] })
        {
            var copia = Ruta(("3S3", 10_000m), ("2S2", 8_000m), ("2", 4_000m));
            Aplicar(copia, Foto("EnCosteo", [.. orden.Select(i => lineas[i])])).Should().BeTrue();

            copia.Vehiculos.Select(v => (v.PesoKg, v.LineaSolicitudNexoId, v.CostoFlete, v.EstadoCosteo))
                .Should().BeEquivalentTo(new (decimal?, string?, decimal?, EstadoCosteo?)[]
                {
                    (10_000m, "N-a", 1_000_000m, EstadoCosteo.Autocosteo), (8_000m, "N-b", 600_000m, EstadoCosteo.Autocosteo),
                    (4_000m, "N-c", 400_000m, EstadoCosteo.Autocosteo),
                });
            var ruta = copia.PrimeraRuta!;
            ruta.CostoFletesTotal.Should().Be(2_000_000m);
            ruta.PrecioFinalLinea.Should().Be(2_352_941m, "2.000.000 con margen del 15 %");
            copia.PrecioFinal.Should().Be(2_352_941m);
            copia.MotivoEstadoCosteo.Should().BeNull();
        }
    }

    [Fact]
    public void LosCamionesIdenticosSeAsocianYSuEstadoSeResumeEnLaRuta()
    {
        var cotizacion = Ruta(("3S3", 10_000m), ("3S3", 10_000m));
        var detalle = new[] { Detalle("N-2", "3S3", 10_000m), Detalle("N-1", "3S3", 10_000m) };

        // Primero la Mesa costea solo una de las dos líneas idénticas.
        Aplicar(cotizacion, Foto("EnCosteo", detalle, [("N-2", 1_000_000m)]));

        cotizacion.Vehiculos.Select(v => v.LineaSolicitudNexoId).Should().Equal("N-1", "N-2");
        cotizacion.Vehiculos.Select(v => v.EstadoCosteo).Should().Equal(EstadoCosteo.PendienteCosteo, EstadoCosteo.Autocosteo);
        cotizacion.PrimeraRuta!.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "una ruta con un camión en la Mesa sigue pendiente");
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().NotBe(clsPrecioCosteoService.MotivoSinAsociar(2, 2), "el empate se resolvió");

        Aplicar(cotizacion, Foto("EnCosteo", detalle, [("N-2", 1_000_000m), ("N-1", 1_000_000m)]));

        cotizacion.Vehiculos.Select(v => v.LineaSolicitudNexoId).Should().Equal("N-1", "N-2");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.PrecioFinal.Should().Be(2_352_941m);
    }

    [Fact]
    public void SiLaMesaLePoneVehiculoALaLineaVaciaSeCopiaAlCamion()
    {
        var cotizacion = Ruta(("3S3", 10_000m), (null, 4_000m));
        var foto = Foto("EnCosteo",
            [("3S3", 10_000m, "N-a", 1_000_000m), ("2", 4_000m, "N-mesa", 500_000m)], carroceriaMesa: ("N-mesa", "ESTACAS"));

        Aplicar(cotizacion, foto);

        var vacio = cotizacion.Vehiculos.Single(v => v.PesoKg == 4_000m);
        (vacio.LineaSolicitudNexoId, vacio.TipoVehiculo, vacio.Carroceria, vacio.CostoFlete).Should().Be(("N-mesa", "2", "ESTACAS", (decimal?)500_000m));
        cotizacion.Vehiculos.Single(v => v.PesoKg == 10_000m).TipoVehiculo.Should().Be("3S3");
        cotizacion.PrecioFinal.Should().Be(1_764_706m, "1.500.000 con margen del 15 %");
    }

    [Fact]
    public void UnCamionDescartadoNoSeLeeNiFrenaElPrecioDeSuRuta()
    {
        var cotizacion = Ruta(("3S3", 10_000m), ("2S2", 8_000m), ("2", 4_000m));
        var descartado = cotizacion.Vehiculos.Single(v => v.PesoKg == 4_000m);
        descartado.RegistrarEstado(EstadoCosteo.Descartada);

        // Nexo trae las dos líneas vigentes y además una de 4 t (la que se descartó en Cotifly).
        Aplicar(cotizacion, Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2S2", 8_000m, "N-b", 600_000m), ("2", 4_000m, "N-c", 400_000m)]));

        descartado.EstadoCosteo.Should().Be(EstadoCosteo.Descartada);
        descartado.LineaSolicitudNexoId.Should().BeNull("un descartado no consume una línea de Nexo");
        descartado.CostoFlete.Should().BeNull();
        var ruta = cotizacion.PrimeraRuta!;
        ruta.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo, "el descartado se ignora en el resumen");
        ruta.CostoFletesTotal.Should().Be(1_600_000m, "ni cuenta en los totales");
        cotizacion.PrecioFinal.Should().Be(1_882_353m);
        cotizacion.MotivoEstadoCosteo.Should().BeNull("el descartado sin línea no cuenta como camión sin asociar");
        clsPrecioCosteoService.Vigentes(cotizacion).Should().NotContain(descartado);
    }

    [Fact]
    public void SiLaMesaCambiaElVehiculoEnNexoSeActualizaElCamion()
    {
        var cotizacion = Ruta(("3S3", 10_000m), ("2S2", 8_000m));
        Aplicar(cotizacion, Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2S2", 8_000m, "N-b", 600_000m)]));

        // Ya asociados, la Mesa cambia el 3S3 por un 2S3 (con carrocería) y el 2S2 vuelve con otras mayúsculas.
        Aplicar(cotizacion, Foto("EnCosteo", [("2S3", 10_000m, "N-a", 1_000_000m), ("2s2", 8_000m, "N-b", 600_000m)], carroceriaMesa: ("N-a", "FURGON")));

        var cambiado = cotizacion.Vehiculos.Single(v => v.LineaSolicitudNexoId == "N-a");
        (cambiado.TipoVehiculo, cambiado.Carroceria).Should().Be(("2S3", "FURGON"));
        cotizacion.Vehiculos.Single(v => v.LineaSolicitudNexoId == "N-b").TipoVehiculo.Should().Be("2S2", "la misma sigla no es un cambio");
    }

    /// <summary>
    /// Nexo devuelve la carrocería del C2 como <c>Furgón</c>. La misma sigla escrita de otra
    /// forma no es un cambio: el camión del cubicador conserva su código canónico.
    /// </summary>
    [Fact]
    public void SoloCambiaLaEscrituraSeConservaElCodigoDelCubicador()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var ruta = cotizacion.PrimeraRuta!;
        var c2 = ruta.AgregarVehiculo("C2", "FURGON", 4_000m, Guid.NewGuid());
        var refrigerado = ruta.AgregarVehiculo("2L2", "FURGON_REFRIGERADO", 1_000m, Guid.NewGuid());

        cotizacion.AplicarFotoNexo(
        [
            new clsCosteoCamionNexo(c2.Id, EstadoCosteo.Autocosteo, true, 500_000m, null, "c2", "Furgón"),
            new clsCosteoCamionNexo(refrigerado.Id, EstadoCosteo.Autocosteo, true, 300_000m, null, "2L2", "furgon-refrigerado"),
        ], _ahora);

        (c2.TipoVehiculo, c2.Carroceria).Should().Be(("C2", "FURGON"), "Furgón es FURGON escrito de otra forma");
        (refrigerado.TipoVehiculo, refrigerado.Carroceria).Should().Be(("2L2", "FURGON_REFRIGERADO"));
        c2.CostoFlete.Should().Be(500_000m, "el costo sí se toma de Nexo");
    }

    /// <summary>Si la Mesa de Costeo cambia el camión, Cotifly adopta el cambio, incluido el tipo de vehículo.</summary>
    [Fact]
    public void SiLaMesaCambiaElVehiculoDeUnCamionDelCubicadorSeAdopta()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var recomendacion = Guid.NewGuid();
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo("2S2", "FURGON", 8_000m, recomendacion);

        cotizacion.AplicarFotoNexo([new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 900_000m, null, "3S3", "Furgón")], _ahora)
            .Valor.Should().BeTrue();

        (camion.TipoVehiculo, camion.Carroceria).Should().Be(("3S3", "FURGON"), "la Mesa manda en el vehículo; la carrocería solo cambió de escritura");
        camion.IdRecomendacionVehiculo.Should().Be(recomendacion, "el vínculo con la línea de Nexo no cambia porque cambie el vehículo");
    }

    /// <summary>
    /// Los porcentajes de llenado del cubicador se calcularon para su vehículo. Si la Mesa cambia de verdad el
    /// vehículo o la carrocería, quedan en <c>null</c> (no se muestra un dato falso); el volumen y el peso de la carga se conservan. Un
    /// cambio solo de escritura o llenar un camión vacío no los toca, y un ciclo siguiente con lo mismo no vuelve a cambiar nada.
    /// </summary>
    [Theory]
    [InlineData("3S3", "FURGON", true)]
    [InlineData("C2", "ESTACAS", true)]
    [InlineData("c2", "Furgón", false)]
    [InlineData(null, null, false)]
    public void SiLaMesaCambiaElVehiculoLosPorcentajesDelCubicadorQuedanEnNull(string? vehiculoNexo, string? carroceriaNexo, bool anula)
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo(new clsCamionPlaneado("C2", "FURGON", 4_000m, Guid.NewGuid(), 22.5m, 81.25m, 47.06m));
        var vacio = cotizacion.PrimeraRuta!.AgregarVehiculo(null, null, 2_000m);

        cotizacion.AplicarFotoNexo(
        [
            new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 500_000m, null, vehiculoNexo, carroceriaNexo),
            new clsCosteoCamionNexo(vacio.Id, EstadoCosteo.Autocosteo, true, 300_000m, null, "C2", "FURGON"),
        ], _ahora);

        camion.PorcentajeLlenadoVolumen.Should().Be(anula ? null : 81.25m);
        camion.PorcentajeLlenadoPeso.Should().Be(anula ? null : 47.06m);
        camion.VolumenTotalM3.Should().Be(22.5m, "el volumen de la carga no depende del vehículo");
        camion.PesoKg.Should().Be(4_000m);
        (vacio.TipoVehiculo, vacio.PorcentajeLlenadoVolumen).Should().Be(("C2", (decimal?)null));

        // Sin oscilación: el ciclo siguiente con la misma foto de Nexo no cambia nada.
        var antes = (camion.TipoVehiculo, camion.Carroceria);
        cotizacion.AplicarFotoNexo([new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 500_000m, null, vehiculoNexo, carroceriaNexo)], _ahora);
        (camion.TipoVehiculo, camion.Carroceria).Should().Be(antes);
        camion.PorcentajeLlenadoVolumen.Should().Be(anula ? null : 81.25m);
    }

    [Fact]
    public void SiLaMesaCambiaLaCarroceriaDeUnCamionDelCubicadorSeAdopta()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo("C2", "FURGON", 4_000m, Guid.NewGuid());

        cotizacion.AplicarFotoNexo([new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 500_000m, null, "C2", "ESTACAS")], _ahora);

        (camion.TipoVehiculo, camion.Carroceria).Should().Be(("C2", "ESTACAS"));
    }

    [Fact]
    public void UnCamionSinVehiculoSeLlenaConLoQueTraeNexo()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var conRecomendacionSinCarroceria = cotizacion.PrimeraRuta!.AgregarVehiculo("2S2", null, 8_000m, Guid.NewGuid());
        var sinVehiculo = cotizacion.PrimeraRuta!.AgregarVehiculo(null, null, 4_000m);

        cotizacion.AplicarFotoNexo(
        [
            new clsCosteoCamionNexo(conRecomendacionSinCarroceria.Id, EstadoCosteo.Autocosteo, true, 900_000m, null, "2S2", "ESTACAS"),
            new clsCosteoCamionNexo(sinVehiculo.Id, EstadoCosteo.Autocosteo, true, 400_000m, null, "C2", "Furgón"),
        ], _ahora);

        (conRecomendacionSinCarroceria.TipoVehiculo, conRecomendacionSinCarroceria.Carroceria).Should().Be(("2S2", "ESTACAS"));
        (sinVehiculo.TipoVehiculo, sinVehiculo.Carroceria).Should().Be(("C2", "Furgón"), "un camión vacío toma lo que puso la Mesa tal cual");
    }

    [Fact]
    public void UnValorVacioDeNexoNoBorraElDeCotifly()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo("C2", "FURGON", 4_000m, Guid.NewGuid());

        cotizacion.AplicarFotoNexo([new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 500_000m, null, " ", null)], _ahora);

        (camion.TipoVehiculo, camion.Carroceria).Should().Be(("C2", "FURGON"));
    }

    /// <summary>
    /// El cruce por <c>grupoRutaId</c> + <c>idRecomendacionVehiculo</c> no depende del vehículo, así que un camión al que la Mesa le
    /// cambió el vehículo (antes o después de adoptarlo) sigue emparejado con su línea.
    /// </summary>
    [Fact]
    public void CambiarElVehiculoNoRompeElCruceConLaLineaDeNexo()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var ruta = cotizacion.PrimeraRuta!;
        var recomendacion = Guid.NewGuid();
        var camion = ruta.AgregarVehiculo("2S2", "FURGON", 8_000m, recomendacion);
        var linea = new clsLineaSolicitudNexo("N-x", null, null, 8_000m, "3S3", "Estacas")
        {
            GrupoRutaId = ruta.Id.ToString("D"),
            IdRecomendacionVehiculo = recomendacion.ToString("D"),
            NumeroLinea = 2,
        };

        // Antes de adoptarlo: el 2S2 de Cotifly contra el 3S3 de Nexo.
        clsEmparejadorLineasNexo.Emparejar([clsCamionParaEmparejar.Desde(ruta, camion)], [linea]).Should().ContainKey(camion.Id)
            .WhoseValue.Should().Be("N-x");

        cotizacion.AplicarFotoNexo([new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 900_000m, null, "3S3", "Estacas")], _ahora);

        (camion.TipoVehiculo, camion.Carroceria).Should().Be(("3S3", "Estacas"));
        clsEmparejadorLineasNexo.Emparejar([clsCamionParaEmparejar.Desde(ruta, camion)], [linea]).Should().ContainKey(camion.Id)
            .WhoseValue.Should().Be("N-x");
    }

    /// <summary>El PDF y el Excel muestran el nombre del catálogo aunque la Mesa haya dejado el nombre o el código escrito distinto.</summary>
    [Theory]
    [InlineData("ESTACAS", "Estacas")]
    [InlineData("Estacas", "Estacas")]
    [InlineData("furgon refrigerado", "Furgón refrigerado")]
    [InlineData("Furgon Refrigerado", "Furgón refrigerado")]
    [InlineData("TANQUE", "TANQUE")]
    [InlineData(null, null)]
    public void ElNombreDelCatalogoSeEncuentraPorCodigoONombreEquivalente(string? guardado, string? esperado)
    {
        var catalogo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ESTACAS"] = "Estacas",
            ["FURGON_REFRIGERADO"] = "Furgón refrigerado",
        };

        clsNombresCatalogoCubicadorExtensiones.Nombre(catalogo, guardado).Should().Be(esperado);
        new clsNombresCatalogoCubicador(null, catalogo).Carroceria(guardado).Should().Be(esperado);
        clsNombresCatalogoCubicadorExtensiones.Nombre(null, guardado).Should().Be(guardado, "sin catálogo se muestra tal cual");
    }

    [Fact]
    public void EnUnCamionSinRecomendacionLaMismaSiglaEscritaDistintoNoEsUnCambio()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo("C2", "FURGON_REFRIGERADO", 4_000m);

        cotizacion.AplicarFotoNexo([new clsCosteoCamionNexo(camion.Id, EstadoCosteo.Autocosteo, true, 500_000m, null, "c2", "Furgón refrigerado")], _ahora);

        (camion.TipoVehiculo, camion.Carroceria).Should().Be(("C2", "FURGON_REFRIGERADO"));
    }

    [Fact]
    public void SiLaMesaBorraLaLineaEnNexoElCamionQuedaDescartadoYNoFrenaNiRecibeConexos()
    {
        // Un camión cuya línea ya no está en la solicitud de Nexo no se queda para siempre en pendiente_costeo: pasa a descartada,
        // sin línea ni costos de Nexo, y los conexos de la ruta van al siguiente camión.
        var cotizacion = Ruta(("3S3", 10_000m), ("2S2", 8_000m));
        Aplicar(cotizacion, Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2S2", 8_000m, "N-b", 1_550_000m)]));
        var primero = cotizacion.PrimeraRuta!.VehiculosOrdenados[0];
        var segundo = cotizacion.PrimeraRuta!.VehiculosOrdenados[1];
        var quedan = (Vehiculo: segundo.TipoVehiculo!, Peso: segundo.PesoKg!.Value, Id: segundo.LineaSolicitudNexoId!);

        Aplicar(cotizacion, Foto("EnCosteo", [(quedan.Vehiculo, quedan.Peso, quedan.Id, 2_550_000m)])).Should().BeTrue();

        (primero.EstadoCosteo, primero.LineaSolicitudNexoId, primero.CostoFlete).Should().Be(((EstadoCosteo?)EstadoCosteo.Descartada, (string?)null, (decimal?)null));
        cotizacion.PrimeraRuta!.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.PrecioFinal.Should().Be(3_000_000m, "solo cuenta el camión que sigue en Nexo");
        clsServiciosConexosCosteoService.PrimerCamion(cotizacion.PrimeraRuta!).Should().BeSameAs(segundo, "el descartado no recibe conexos");
        clsPrecioCosteoService.CamionesConLineaBorrada(cotizacion, Foto("EnCosteo", [(quedan.Vehiculo, quedan.Peso, quedan.Id, 1m)])).Should().BeEmpty();
        clsPrecioCosteoService.CamionesConLineaBorrada(cotizacion, new clsCosteoConsultadoNexo("Descartada", [], null)).Should().BeEmpty(
            "una solicitud descartada (o una foto sin líneas) no borra líneas");
    }

    [Fact]
    public void SiTodosEstanDescartadosSeLeenTodos()
    {
        var cotizacion = Ruta(("3S3", 10_000m), ("2S2", 8_000m));
        foreach (var camion in cotizacion.Vehiculos)
        {
            camion.RegistrarEstado(EstadoCosteo.Descartada);
        }

        clsPrecioCosteoService.Vigentes(cotizacion).Should().HaveCount(2);
    }

    [Fact]
    public void LosConexosDeNexoVuelvenALaRutaDelCamionCuyaLineaLosLleva()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var segunda = cotizacion.AgregarRuta(new clsLineaSolicitudCosteo { Origen = "Cali", Destino = "Yumbo", PesoKg = 5_000m, TipoVehiculo = "2" }, 1, 3);
        segunda.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        cotizacion.PrimeraRuta!.Vehiculos[0].AsignarLineaNexo("LS-1");
        segunda.Vehiculos[0].AsignarLineaNexo("LS-2");
        var nexo = new clsCotizacionNexo("COT", "Borrador", "SinDefinir",
            [new clsLineaCosteoNexo("LS-1", 1m, null, null, "LC-1"), new clsLineaCosteoNexo("LS-2", 1m, null, null, "LC-2")], []);
        var deLaSegunda = new clsConexoNexo("CX-2", "Escolta", "logistico", 1m, 50_000m) { CotizacionLineaId = "LC-2" };
        var sinLinea = new clsConexoNexo("CX-9", "Precinto", "adicional", 1m, 10_000m);

        clsServiciosConexosCosteoService.RutaDelConexo(cotizacion, nexo, deLaSegunda).Should().BeSameAs(segunda);
        clsServiciosConexosCosteoService.RutaDelConexo(cotizacion, nexo, sinLinea).Should().BeNull();

        var importados = clsServiciosConexosCosteoService.ImportarConexosPorRuta(cotizacion, nexo, [deLaSegunda, sinLinea], cotizacion.PrimeraRuta!, _ahora);

        importados.Valor.Should().Be(2);
        segunda.Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-2");
        cotizacion.PrimeraRuta!.Conexos.Select(c => c.IdServicioConexo).Should().Equal(new[] { "CX-9" }, "sin línea va a la ruta por defecto");
    }

    [Fact]
    public void LaNotaDelEnvioLeDiceALaMesaQueLineasSonLaMismaRuta()
    {
        clsEnvioCosteoService.NotaCamionesPorRuta(Ruta(("3S3", 10_000m))).Should().BeNull("con un camión por ruta no hay nada que agrupar");

        var cotizacion = Ruta(("3S3", 10_000m), ("3S3", 10_000m), ("2", 4_000m));
        cotizacion.AgregarRuta(new clsLineaSolicitudCosteo { Origen = "Cali", Destino = "Yumbo", PesoKg = 5_000m }, 1, 3);

        clsEnvioCosteoService.NotaCamionesPorRuta(cotizacion).Should().Be(
            "Cotifly: una línea por camión, en este orden. Ruta 1 (Buenaventura → Bogotá, D.C.): 3 camiones. Ruta 2 (Cali → Yumbo): 1 camión. "
            + clsEnvioCosteoService.AvisoSinPosiciones,
            "nombra todas las rutas, también las de un camión");
    }

    /// <summary>
    /// El camión del cubicador guarda <c>C2</c> y viajó como <c>2</c> (traducido por el alias de Nexo). Con la
    /// equivalencia que registró el envío se empareja con la línea de Nexo que dice <c>2</c>, conserva su <c>C2</c> y sus porcentajes de
    /// llenado (no es un cambio de la Mesa) y las lecturas siguientes no cambian nada (sin oscilación).
    /// </summary>
    [Fact]
    public void ElCamionTraducidoPorAliasSeEmparejaYNoOscila()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo(new clsCamionPlaneado("C2", null, 4_000m, Guid.NewGuid(), 22.5m, 81.25m, 47.06m));
        var foto = Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2", 4_000m, "N-b", 400_000m)]) with
        {
            EquivalenciasVehiculo = new Dictionary<string, string> { ["C2"] = "2" },
        };

        Aplicar(cotizacion, foto).Should().BeTrue();

        camion.LineaSolicitudNexoId.Should().Be("N-b", "C2 viajó como 2: el emparejador compara con el código que viajó");
        (camion.TipoVehiculo, camion.PorcentajeLlenadoVolumen, camion.PorcentajeLlenadoPeso).Should().Be(("C2", (decimal?)81.25m, (decimal?)47.06m),
            "Nexo devolvió el mismo código que se le envió: no es un cambio de la Mesa");
        cotizacion.MotivoEstadoCosteo.Should().NotBe(clsPrecioCosteoService.MotivoSinAsociar(1, 2));
        cotizacion.PrecioFinal.Should().NotBeNull();

        for (var ciclo = 0; ciclo < 3; ciclo++)
        {
            Aplicar(cotizacion, foto).Should().BeFalse("la misma foto no cambia nada (ciclo {0})", ciclo);
            camion.TipoVehiculo.Should().Be("C2");
        }
    }

    [Fact]
    public void SinLaEquivalenciaElCamionTraducidoNoSeEmparejaPorContenido()
    {
        // Lo que pasaría sin registrar la traducción: C2 contra 2 es una contradicción y el camión queda sin asociar.
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo(new clsCamionPlaneado("C2", null, 4_000m, Guid.NewGuid()));

        Aplicar(cotizacion, Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2", 4_000m, "N-b", 400_000m)]));

        camion.LineaSolicitudNexoId.Should().BeNull();
    }

    [Fact]
    public void SiLaMesaCambiaDeVerdadElVehiculoTraducidoSeAdopta()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.PrimeraRuta!.AgregarVehiculo(new clsCamionPlaneado("C2", null, 4_000m, Guid.NewGuid(), 22.5m, 81.25m, 47.06m));
        var equivalencias = new Dictionary<string, string> { ["C2"] = "2" };
        Aplicar(cotizacion, Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2", 4_000m, "N-b", 400_000m)]) with { EquivalenciasVehiculo = equivalencias });

        var cambiada = Foto("EnCosteo", [("3S3", 10_000m, "N-a", 1_000_000m), ("2S2", 4_000m, "N-b", 450_000m)]) with { EquivalenciasVehiculo = equivalencias };
        Aplicar(cotizacion, cambiada).Should().BeTrue();

        (camion.TipoVehiculo, camion.PorcentajeLlenadoVolumen).Should().Be(("2S2", (decimal?)null), "D2: la Mesa manda");
        Aplicar(cotizacion, cambiada).Should().BeFalse("y la lectura siguiente no vuelve a cambiar");
    }

    // ------------------------------------------------------------------ Apoyo

    private bool Aplicar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, _ahora, new clsOpcionesCosteoNexo(), _servicio);

    /// <summary>Formulario de una ruta (Buenaventura → Bogotá) con un camión por (vehículo, peso), enviado y con margen del 15 %.</summary>
    private static clsCotizacionSolicitud Ruta(params (string? Vehiculo, decimal Peso)[] camiones)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CAM-08102026-001" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [new clsLineaSolicitudCosteo { Origen = "Buenaventura", Destino = "Bogotá, D.C.", TipoVehiculo = camiones[0].Vehiculo, PesoKg = camiones[0].Peso }],
            [0], 3);
        var ruta = cotizacion.PrimeraRuta!;
        foreach (var camion in camiones.Skip(1))
        {
            ruta.AgregarVehiculo(camion.Vehiculo, null, camion.Peso);
        }

        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        return cotizacion;
    }

    /// <summary>Línea de Nexo de la ruta Buenaventura → Bogotá como la devuelve Nexo (mayúsculas, sin tildes).</summary>
    private static clsLineaSolicitudNexo Detalle(string id, string? vehiculo, decimal peso) =>
        new(id, "BUENAVENTURA → BOGOTA, D.C.", null, peso, vehiculo, null);

    /// <summary>Foto con el contenido de cada línea (en el orden dado) y su flete; <paramref name="carroceriaMesa"/> la pone la Mesa.</summary>
    private static clsCosteoConsultadoNexo Foto(
        string estado, IReadOnlyList<(string Vehiculo, decimal Peso, string Id, decimal Flete)> lineas, (string Id, string Carroceria)? carroceriaMesa = null) =>
        Foto(estado,
            [.. lineas.Select(l => Detalle(l.Id, l.Vehiculo, l.Peso) with
            {
                Carroceria = carroceriaMesa is { } mesa && mesa.Id == l.Id ? mesa.Carroceria : null,
            })],
            [.. lineas.Select(l => (l.Id, l.Flete))]);

    private static clsCosteoConsultadoNexo Foto(string estado, IReadOnlyList<clsLineaSolicitudNexo> detalle, IReadOnlyList<(string Id, decimal Flete)> fletes) =>
        new(estado, [.. detalle.Select(d => d.Id)], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], []))
        {
            Detalle = detalle,
        };
}
