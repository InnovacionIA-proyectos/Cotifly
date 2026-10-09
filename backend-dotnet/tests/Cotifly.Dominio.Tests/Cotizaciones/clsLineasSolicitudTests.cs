using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>Líneas de la solicitud de Nexo (HU-011.2, ADR-017): validación por campo, entidad, derivación por línea y archivo.</summary>
public sealed class clsLineasSolicitudTests
{
    private static clsLineaSolicitudCosteo Linea(decimal? peso = 9000m) => new() { Origen = "YUMBO", Destino = "BOGOTA, D.C.", PesoKg = peso };

    [Fact]
    public void UnaLineaValidaNoTieneErrores() => Linea().Errores().Should().BeEmpty();

    [Fact]
    public void ReportaTodosLosErroresDeLaLineaNoSoloElPrimero()
    {
        var linea = new clsLineaSolicitudCosteo
        {
            Origen = "",
            Destino = new string('x', 151),
            TipoVehiculo = new string('v', 101),
            ValorMercancia = 1.234m,
        };

        var errores = linea.Errores();

        errores.Select(e => (e.Campo, e.Codigo)).Should().BeEquivalentTo(new[]
        {
            ("origen", clsCodigosLineaSolicitud.LineaIncompleta),
            ("destino", clsCodigosLineaSolicitud.CampoDemasiadoLargo),
            ("tipo_vehiculo", clsCodigosLineaSolicitud.CampoDemasiadoLargo),
            ("valor_mercancia", clsCodigosLineaSolicitud.ValorFueraDeRango),
            ("peso_kg", clsCodigosLineaSolicitud.PesoRequerido),
        });
    }

    [Fact]
    public void LaReglaDePesoEsLaDeNexoConTolerancia()
    {
        var posiciones = new[] { new clsPosicionCarga(null, 10, 100, 100, 100, 100m) };

        (Linea(peso: null) with { Posiciones = posiciones }).Errores().Should().BeEmpty("sin peso, la suma de las posiciones es el peso");
        (Linea(peso: 1005m) with { Posiciones = posiciones }).Errores().Should().BeEmpty("0,5 % de diferencia está dentro del 1 %");
        (Linea(peso: 1100m) with { Posiciones = posiciones }).Errores().Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.PesoInconsistente);
        Linea(peso: 0m).Errores().Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.PesoRequerido);
    }

    [Fact]
    public void LasPosicionesInvalidasYDeMasSeReportanPorCampo()
    {
        var linea = Linea() with
        {
            Posiciones = [.. Enumerable.Range(0, 201).Select(i => new clsPosicionCarga(null, i == 0 ? 0 : 1, 10, 10, 10, null))],
        };

        var errores = linea.Errores();

        errores.Should().Contain(e => e.Campo == "posiciones" && e.Codigo == clsCodigosLineaSolicitud.DemasiadasPosiciones);
        errores.Should().Contain(e => e.Campo == "posiciones[0]" && e.Codigo == clsCodigosLineaSolicitud.PosicionInvalida);
    }

    [Fact]
    public void ElFormularioDeUnaLineaSeDescomponeYSeRearmaSinPerderDatos()
    {
        var formulario = new clsSolicitudCosteo
        {
            Tipo = TipoSolicitud.Spot,
            HorasCargue = 4m,
            Origen = "YUMBO",
            Destino = "CALI",
            TipoVehiculo = "3S3",
            PesoKg = 100m,
            Posiciones = [new clsPosicionCarga("Caja", 1, 10, 10, 10, 100m)],
        };

        clsSolicitudCosteo.Desde(formulario.Encabezado, formulario.ALinea()).Should().BeEquivalentTo(formulario);
    }

    [Fact]
    public void ElEncabezadoValidaLasHoras()
    {
        // Decisión 1 de HU-021: referencia y valor estimado se eliminan del encabezado.
        var encabezado = new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion, HorasCargue = 241m };

        encabezado.Errores().Select(e => e.Campo).Should().BeEquivalentTo(["horas_cargue"]);
    }

    [Fact]
    public void LasRutasDeLaCotizacionConservanElOrdenLaFilaYLosCampos()
    {
        var cotizacion = Solicitud([Linea(), Linea(500m) with { Origen = " CALI " }], [3, null]);
        var rutas = cotizacion.RutasOrdenadas;

        rutas.Select(l => (l.Orden, l.RutaOrigenIndice, l.OrigenNombre)).Should().Equal((1, 3, "YUMBO"), (2, (int?)null, "CALI"));
        rutas.Should().OnlyContain(l => l.CotizacionSolicitudId == cotizacion.Id && l.PrecioFinalLinea == null && l.Vehiculos.Count == 1);
        rutas.Should().OnlyContain(l => l.EstadoCosteo == EstadoCosteo.PendienteEnvio, "lo guardado queda por enviar");
        var deVuelta = rutas[1].ALinea();
        deVuelta.PesoKg.Should().Be(500m);
    }

    [Fact]
    public void SinFleteElCamionPierdeElCostoYLaRutaElPrecio()
    {
        var cotizacion = Solicitud([Linea()]);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        var camion = ruta.Vehiculos[0];
        camion.RegistrarCosteo(EstadoCosteo.Autocosteo, 1000m, null).Should().BeTrue();
        ruta.RecalcularTotales(null, 0m).Valor.Should().BeTrue();
        ruta.PrecioFinalLinea.Should().Be(1176m);
        camion.TieneCosteo.Should().BeTrue();

        camion.RegistrarCosteo(EstadoCosteo.PendienteCosteo, null, null).Should().BeTrue();
        ruta.RecalcularTotales(null, 0m);

        ruta.PrecioFinalLinea.Should().BeNull();
        camion.TieneCosteo.Should().BeFalse();
        camion.RegistrarCosteo(EstadoCosteo.PendienteCosteo, null, null).Should().BeFalse("la misma foto no cambia nada");
    }

    [Fact]
    public void LaDerivacionPorLineaUsaElFleteDeEsaLinea()
    {
        var nexo = new clsCotizacionNexo("C", clsCotizacionNexo.EstadoBorrador, "SinDefinir",
            [new clsLineaCosteoNexo("L1", 1000m, 200m, null)], []);
        var foto = new clsCosteoConsultadoNexo("EnCosteo", ["L1", "L2"], nexo);

        clsDerivacionEstadoCosteo.Derivar(foto).Should().Be(EstadoCosteo.PendienteCosteo);
        clsDerivacionEstadoCosteo.DerivarLinea(foto, "L1").Should().Be(EstadoCosteo.Autocosteo);
        clsDerivacionEstadoCosteo.DerivarLinea(foto, "L2").Should().Be(EstadoCosteo.PendienteCosteo);
        clsDerivacionEstadoCosteo.CostoLinea(foto, "L1").Should().Be(1200m);
        clsDerivacionEstadoCosteo.CostoLinea(foto, "L2").Should().BeNull();
        clsDerivacionEstadoCosteo.CostosCamion(foto, "L1").Should().Be((1000m, 0m), "el levante no sale de Nexo: es la copia de Cotifly (decisión 4)");
        clsDerivacionEstadoCosteo.DerivarLinea(foto with { EstadoSolicitud = "Descartada" }, "L1").Should().Be(EstadoCosteo.Descartada);
    }

    [Fact]
    public void ElEmparejadorAsociaPorRutaNormalizadaYDatosCompatiblesSinImportarElOrden()
    {
        var lineas = Camiones(
        [
            new clsLineaSolicitudCosteo { Origen = "Bogotá, D.C.", Destino = "Medellín", PesoKg = 1000m, TipoVehiculo = "3S3" },
            new clsLineaSolicitudCosteo { Origen = "Bogotá, D.C.", Destino = "Medellín", PesoKg = 2000m, TipoVehiculo = "3S3" },
            new clsLineaSolicitudCosteo { Origen = "Cali", Destino = "Yumbo", PesoKg = 5m },
        ]);
        clsLineaSolicitudNexo[] nexo =
        [
            new("N3", "CALI -> YUMBO", "CargaSeca", 5m, "2", "FURGON"),
            new("N2", "BOGOTA,  D.C. → MEDELLIN", null, 2000m, "3s3", null),
            new("N1", "bogota, d.c. → medellin", null, 1000.00m, null, null),
        ];

        var pares = clsEmparejadorLineasNexo.Emparejar(lineas, nexo);

        pares.Should().HaveCount(3);
        pares[lineas[0].Id].Should().Be("N1", "misma ruta: decide el peso");
        pares[lineas[1].Id].Should().Be("N2");
        pares[lineas[2].Id].Should().Be("N3", "lo que Cotifly no envió (tipología, carrocería) no contradice");
    }

    [Fact]
    public void ElEmparejadorNoAsociaLineasIdenticasNiDatosQueSeContradicen()
    {
        var lineas = Camiones(
        [
            new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", PesoKg = 1m },
            new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", PesoKg = 1m },
            new clsLineaSolicitudCosteo { Origen = "C", Destino = "D", PesoKg = 1m, TipoVehiculo = "3S3" },
        ]);
        clsLineaSolicitudNexo[] nexo = [new("N1", "A → B", null, 1m, null, null), new("N2", "A → B", null, 1m, null, null), new("N3", "C → D", null, 1m, "2", null)];

        clsEmparejadorLineasNexo.Emparejar(lineas, nexo).Should().BeEmpty("dos idénticas son ambiguas y la tercera contradice el vehículo");
    }

    [Fact]
    public void LasMercanciasCuelganDeSuRutaConUnOrdenPropio()
    {
        // HU-021: el orden es único por ruta (uq_cotizacionMercancias_lineaSolicitudId_orden), ya no por cotización.
        var posicion = new clsPosicionCarga(null, 1, 1, 1, 1, null);
        var cotizacion = Solicitud([Linea() with { Posiciones = [posicion, posicion] }, Linea() with { Posiciones = [posicion] }]);

        cotizacion.RutasOrdenadas.Select(r => string.Join(",", r.MercanciasOrdenadas.Select(m => m.Orden))).Should().Equal("1,2", "1");
        cotizacion.Rutas.Should().OnlyContain(r => r.Mercancias.All(m => m.LineaSolicitudId == r.Id));
    }

    private static clsCotizacionSolicitud Solicitud(IReadOnlyList<clsLineaSolicitudCosteo> lineas, IReadOnlyList<int?>? filas = null)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-LIC-1" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion }, lineas, filas, 3);
        return cotizacion;
    }

    private static IReadOnlyList<clsCamionParaEmparejar> Camiones(IReadOnlyList<clsLineaSolicitudCosteo> lineas) =>
        [.. Solicitud(lineas).RutasOrdenadas.Select(r => clsCamionParaEmparejar.Desde(r, r.Vehiculos[0]))];

    [Theory]
    [InlineData(null, 10, "Hoja", clsCodigosError.ArchivoRequerido)]
    [InlineData("pliego.xls", 10, "Hoja", clsCodigosError.FormatoNoSoportado)]
    [InlineData("pliego.xlsx", clsArchivoLicitacion.TamanoMaximoBytes + 1, "Hoja", clsCodigosError.ArchivoDemasiadoGrande)]
    [InlineData("pliego.xlsx", 10, null, clsCodigosError.LicitacionInvalida)]
    public void ElArchivoSeValidaComoEnNexo(string? nombre, int tamano, string? hoja, string codigo) =>
        clsArchivoLicitacion.Crear(Guid.NewGuid(), nombre ?? "pliego.xlsx", nombre is null ? [] : new byte[tamano], hoja).Error.Codigo.Should().Be(codigo);

    [Fact]
    public void ElArchivoGuardaSoloElNombreYSuHuella()
    {
        var archivo = clsArchivoLicitacion.Crear(Guid.NewGuid(), @"C:\Users\ana\Pliego.XLSM", [1, 2, 3], " Pliego ").Valor;

        archivo.NombreArchivo.Should().Be("Pliego.XLSM");
        archivo.TipoContenido.Should().Be(clsArchivoLicitacion.TipoXlsm);
        archivo.Hoja.Should().Be("Pliego");
        archivo.TamanoBytes.Should().Be(3);
        archivo.Sha256.Should().HaveLength(64);
    }
}
