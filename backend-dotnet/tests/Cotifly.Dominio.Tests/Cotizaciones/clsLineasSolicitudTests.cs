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
            Referencia = "OC-1",
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
    public void ElEncabezadoValidaReferenciaYHoras()
    {
        var encabezado = new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion, Referencia = new string('r', 201), HorasCargue = 241m };

        encabezado.Errores().Select(e => e.Campo).Should().BeEquivalentTo(["referencia", "horas_cargue"]);
    }

    [Fact]
    public void LasLineasDeLaCotizacionConservanElOrdenLaFilaYLosCampos()
    {
        var cotizacionId = Guid.NewGuid();
        var lineas = clsCotizacionLineaSolicitud.Desde(cotizacionId, [Linea(), Linea(500m) with { Origen = " CALI " }], [3, null]);

        lineas.Select(l => (l.Orden, l.FilaOrigen, l.Origen)).Should().Equal((1, 3, "YUMBO"), (2, (int?)null, "CALI"));
        lineas.Should().OnlyContain(l => l.CotizacionId == cotizacionId && l.EstadoCosteo == null && l.PrecioFinal == null);
        var deVuelta = lineas[1].ALinea([]);
        deVuelta.PesoKg.Should().Be(500m);
    }

    [Fact]
    public void SinFleteLaLineaPierdeElPrecio()
    {
        var linea = clsCotizacionLineaSolicitud.Desde(Guid.NewGuid(), [Linea()])[0];
        linea.RegistrarCosteo(EstadoCosteo.Autocosteo, 1000m).Should().BeTrue();
        linea.RegistrarPrecio(1176m).Should().BeTrue();
        linea.TieneCosteo.Should().BeTrue();

        linea.RegistrarCosteo(EstadoCosteo.PendienteCosteo, null).Should().BeTrue();

        linea.PrecioFinal.Should().BeNull();
        linea.TieneCosteo.Should().BeFalse();
        linea.RegistrarCosteo(EstadoCosteo.PendienteCosteo, null).Should().BeFalse("la misma foto no cambia nada");
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
        clsDerivacionEstadoCosteo.DerivarLinea(foto with { EstadoSolicitud = "Descartada" }, "L1").Should().Be(EstadoCosteo.Descartada);
    }

    [Fact]
    public void LasPosicionesDeVariasLineasSiguenElOrdenDeLaCotizacion()
    {
        var cotizacion = Guid.NewGuid();
        var linea = Guid.NewGuid();

        var posiciones = clsCotizacionPosicion.Desde(cotizacion, [new clsPosicionCarga(null, 1, 1, 1, 1, null), new clsPosicionCarga(null, 1, 1, 1, 1, null)], linea, 4);

        posiciones.Select(p => p.Orden).Should().Equal(4, 5);
        posiciones.Should().OnlyContain(p => p.LineaSolicitudId == linea);
    }

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
