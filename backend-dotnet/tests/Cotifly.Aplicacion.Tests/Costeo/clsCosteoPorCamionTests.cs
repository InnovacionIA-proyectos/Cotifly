using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Lectura del costeo de Nexo camión por camión (HU-021.7, #134): N camiones por ruta con las líneas de Nexo en cualquier orden, empates
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

        clsEnvioCosteoService.NotaCamionesPorRuta(cotizacion).Should().Be("Cotifly: una línea por camión. Ruta 1 (Buenaventura → Bogotá, D.C.): 3 camiones.");
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
