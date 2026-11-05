using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// #278 (fase 0 del contrato único de NEXO, DU §5 y §8): línea de negocio cerrada a seis, <c>pesoKg</c> por unidad obligatorio en una
/// mercancía con medidas, horas de cargue y descargue en pareja y el tope de 10.000 unidades por línea.
/// </summary>
public sealed class clsContratoNexoFase0Tests
{
    [Theory]
    [InlineData("Nacional", "Nacional")]
    [InlineData("PuertoAInterior", "Puerto a interior")]
    [InlineData("puertoAInterior", "Puerto a interior")]
    [InlineData("INTERIOR-A-PUERTO", "Interior a puerto")]
    [InlineData("InteriorAPuerto", "Interior a puerto")]
    [InlineData(" Devolución de contenedor ", "Devolución de contenedor")]
    [InlineData("DevolucionDeContenedor", "Devolución de contenedor")]
    [InlineData("devolucion_de_contenedor", "Devolución de contenedor")]
    [InlineData("ANDINO", "Andino")]
    [InlineData("licitaciones", "Licitaciones")]
    public void LasSeisLineasDeNexoSeReconocenComoNexo(string valor, string canonica)
    {
        clsLineasNegocioNexo.Canonica(valor).Should().Be(canonica);
        clsLineasNegocioNexo.EsValida(valor).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinLineaEsValido(string? valor)
    {
        clsLineasNegocioNexo.EsValida(valor).Should().BeTrue();
        clsLineasNegocioNexo.Canonica(valor).Should().BeNull();
    }

    [Theory]
    [InlineData("Importacion")]
    [InlineData("Puerto")]
    [InlineData("Oferta de Expedición Puerto a interior")]
    [InlineData("Licitacion")]
    public void OtroTextoNoEsUnaLineaDeNexo(string valor)
    {
        clsLineasNegocioNexo.EsValida(valor).Should().BeFalse();
        clsLineasNegocioNexo.Canonica(valor).Should().BeNull();
    }

    [Fact]
    public void LaSemillaActualDeMapeoLineasNexoEsValida() =>
        new[] { "Nacional", "PuertoAInterior", "InteriorAPuerto", "Andino", null }.Should().OnlyContain(v => clsLineasNegocioNexo.EsValida(v));

    [Fact]
    public void UnaPoliticaNoAceptaUnMapeoANexoFueraDeLasSeis()
    {
        var invalida = clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: "Importacion");

        invalida.EsFallo.Should().BeTrue();
        invalida.Error.Codigo.Should().Be(clsMargenPolitica.CodigoError);
        invalida.Error.Mensaje.Should().Contain("mapeoLineasNexo").And.Contain("Puerto a interior");
        clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: "PuertoAInterior").EsExitoso.Should().BeTrue();
        clsMargenPolitica.Crear("Contenedores", 0.15m).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public void UnaMercanciaConMedidasExigeElPesoPorUnidad()
    {
        var sinPeso = new clsPosicionCarga("Caja", 3, 50m, 40m, 30m, null);

        var error = sinPeso.PrimerError(2);

        error.Should().NotBeNull();
        error!.Campo.Should().Be("peso_kg");
        error.Mensaje.Should().StartWith("Mercancía 2: ").And.Contain("una unidad");
        (sinPeso with { PesoKg = 0m }).PrimerError(1)!.Campo.Should().Be("peso_kg");
        (sinPeso with { PesoKg = 12.5m }).PrimerError(1).Should().BeNull();
    }

    [Fact]
    public void UnaMercanciaSinMedidasNoExigePeso() =>
        new clsPosicionCarga("Bultos varios", 30, 0m, 0m, 0m, null).PrimerError(1).Should().BeNull();

    [Fact]
    public void LaLineaReportaLaMercanciaConMedidasSinPeso()
    {
        var linea = Linea() with { Posiciones = [new clsPosicionCarga("Caja", 3, 50m, 40m, 30m, 100m), new clsPosicionCarga("Tubo", 1, 600m, 20m, 20m, null)] };

        linea.Errores().Should().ContainSingle(e => e.Campo == "mercancias[1].peso_kg" && e.Codigo == clsCodigosLineaSolicitud.PosicionInvalida);
    }

    [Fact]
    public void MasDeDiezMilUnidadesConMedidasEsCubicacionDemasiadoGrande()
    {
        var linea = Linea() with
        {
            PesoKg = 10_001m,
            Posiciones = [new clsPosicionCarga("Caja", 6_000, 10m, 10m, 10m, 1m), new clsPosicionCarga("Caja", 4_001, 10m, 10m, 10m, 1m)],
        };

        var error = linea.Errores().Should().ContainSingle(e => e.Codigo == clsCodigosError.CubicacionDemasiadoGrande).Subject;
        error.Campo.Should().Be(clsLineaSolicitudCosteo.CampoMercancias);
        error.Mensaje.Should().Contain("10.000");
        (linea with { PesoKg = 10_000m, Posiciones = [new clsPosicionCarga("Caja", 10_000, 10m, 10m, 10m, 1m)] }).Errores().Should().BeEmpty();
    }

    [Fact]
    public void ElTopeDeUnidadesNoAplicaAMercanciasSinMedidasQueNoViajanComoPosiciones() =>
        (Linea() with { Posiciones = [new clsPosicionCarga("Tornillos", 50_000, 0m, 0m, 0m, null)] }).Errores().Should().BeEmpty();

    [Fact]
    public void ElFormularioDeUnaLineaTambienAplicaElTopeDeUnidades()
    {
        var solicitud = clsSolicitudCosteo.Desde(Encabezado(4m, 4m), Linea() with
        {
            PesoKg = 10_001m,
            Posiciones = [new clsPosicionCarga("Caja", 10_001, 10m, 10m, 10m, 1m)],
        });

        var error = solicitud.Validar();

        error.Should().NotBeNull();
        error!.Detalle.Should().Be(clsCodigosError.CubicacionDemasiadoGrande);
    }

    [Theory]
    [InlineData(4.0, null, "horas_descargue")]
    [InlineData(null, 2.5, "horas_cargue")]
    public void LasHorasDelEncabezadoVanEnPareja(double? cargue, double? descargue, string falta)
    {
        var errores = Encabezado((decimal?)cargue, (decimal?)descargue).Errores();

        var error = errores.Should().ContainSingle().Subject;
        error.Campo.Should().Be(falta);
        error.Codigo.Should().Be(clsCodigosLineaSolicitud.CampoRequerido);
        error.Mensaje.Should().Contain("las dos");
    }

    [Fact]
    public void SinHorasOConLasDosElEncabezadoEsValido()
    {
        Encabezado(null, null).Errores().Should().BeEmpty();
        Encabezado(0m, 240m).Errores().Should().BeEmpty();
        Encabezado(241m, 1m).Errores().Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.ValorFueraDeRango);
    }

    [Fact]
    public void LasHorasDeLaLineaVanEnPareja()
    {
        (Linea() with { HorasCargue = 1m }).Errores().Should().ContainSingle(e => e.Campo == "horas_descargue" && e.Codigo == clsCodigosLineaSolicitud.CampoRequerido);
        (Linea() with { HorasCargue = 1m, HorasDescargue = 1m }).Errores().Should().BeEmpty();
    }

    [Fact]
    public void ElFormularioDeUnaLineaExigeLasHorasEnPareja()
    {
        var solicitud = clsSolicitudCosteo.Desde(Encabezado(4m, null), Linea());

        solicitud.Validar()!.Mensaje.Should().Contain("horas_descargue");
        clsSolicitudCosteo.Desde(Encabezado(4m, 4m), Linea()).Validar().Should().BeNull();
    }

    private static clsEncabezadoSolicitudCosteo Encabezado(decimal? cargue, decimal? descargue) =>
        new() { Tipo = TipoSolicitud.Spot, HorasCargue = cargue, HorasDescargue = descargue };

    private static clsLineaSolicitudCosteo Linea() => new() { Origen = "Cali", Destino = "Bogotá", PesoKg = 1_000m };
}
