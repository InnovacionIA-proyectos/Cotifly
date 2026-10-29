using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-013.11 (#108): las filas de una licitación cumplen las reglas de captura del formulario. G3 (contenedor y aprovisionamiento)
/// por fila con su código, y G5 (horas de cargue y descargue propias de la línea) guardadas y reenviadas.
/// </summary>
public sealed class clsCapturaFilasLicitacionTests
{
    private static clsLineaSolicitudCosteo Linea() => new() { Origen = "BUENAVENTURA", Destino = "BOGOTA, D.C.", PesoKg = 9000m };

    [Fact]
    public void UnaFilaDeContenedorConSuClaseNoTieneErroresCondicionales() =>
        (Linea() with { ClaseViaje = "Contenedor", ClaseContenedor = "CNT40.HC" }).Errores().Should().BeEmpty();

    [Fact]
    public void LaClaseDeContenedorEsObligatoriaConContenedor() =>
        (Linea() with { ClaseViaje = " Contenedor " }).Errores().Select(e => (e.Campo, e.Codigo))
            .Should().Equal(("clase_contenedor", clsCodigosLineaSolicitud.CampoRequerido));

    [Theory]
    [InlineData(null)]
    [InlineData("Carga Suelta")]
    [InlineData("contenedor")]
    public void LaClaseDeContenedorSoloAplicaConContenedor(string? claseViaje) =>
        (Linea() with { ClaseViaje = claseViaje, ClaseContenedor = "CNT20.FT" }).Errores().Select(e => (e.Campo, e.Codigo))
            .Should().Equal(("clase_contenedor", clsCodigosLineaSolicitud.CampoNoAplica));

    [Theory]
    [InlineData(null)]
    [InlineData("buenaventura ")]
    public void ElAprovisionamientoSoloAplicaConUnLevanteDistintoDelOrigen(string? levante) =>
        (Linea() with { CiudadLevanteContenedor = levante, ValorAprovisionamiento = 150_000m }).Errores().Select(e => (e.Campo, e.Codigo))
            .Should().Equal(("valor_levante_contenedor", clsCodigosLineaSolicitud.CampoNoAplica));

    [Fact]
    public void ConLevanteDistintoElAprovisionamientoSeAcepta() =>
        (Linea() with { CiudadLevanteContenedor = "YUMBO", ValorAprovisionamiento = 150_000m }).Errores().Should().BeEmpty();

    [Fact]
    public void UnaFilaReportaJuntosLosErroresDeContenedorYDeAprovisionamiento() =>
        (Linea() with { ClaseViaje = "Cupo", ClaseContenedor = "CNT40.HC", ValorAprovisionamiento = 1m }).Errores().Select(e => e.Campo)
            .Should().Equal("clase_contenedor", "valor_levante_contenedor");

    [Fact]
    public void ElFormularioUsaLaMismaReglaConElMismoMensaje()
    {
        var formulario = clsSolicitudCosteo.Desde(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot }, Linea() with { ClaseViaje = "Cupo", ClaseContenedor = "CNT40.HC" });

        formulario.Validar()!.Mensaje.Should().Be((Linea() with { ClaseViaje = "Cupo", ClaseContenedor = "CNT40.HC" }).ErroresCondicionales()[0].Mensaje);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(240.01)]
    [InlineData(2.555)]
    public void LasHorasDeLaFilaTienenElRangoDeNexo(double horas)
    {
        var errores = (Linea() with { HorasCargue = (decimal)horas, HorasDescargue = (decimal)horas }).Errores();

        errores.Select(e => (e.Campo, e.Codigo)).Should().BeEquivalentTo(new[]
        {
            ("horas_cargue", clsCodigosLineaSolicitud.ValorFueraDeRango),
            ("horas_descargue", clsCodigosLineaSolicitud.ValorFueraDeRango),
        });
    }

    [Fact]
    public void LaLineaGuardadaConservaSusHorasYLasDevuelveParaElReintento()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-LIC-2" }).Valor;
        var guardada = cotizacion.AgregarRuta(Linea() with { HorasCargue = 2m, HorasDescargue = 3.5m, TipoVehiculo = " 3S3 " }, 0, 3);

        guardada.HorasCargue.Should().Be(2m);
        guardada.HorasDescargue.Should().Be(3.5m);
        var reenviada = guardada.ALinea();
        reenviada.HorasCargue.Should().Be(2m);
        reenviada.HorasDescargue.Should().Be(3.5m);
        reenviada.TipoVehiculo.Should().Be("3S3", "se reenvía lo guardado, recortado");
    }

    [Fact]
    public void LasHorasDeLaLineaMandanSobreLasDelEncabezado()
    {
        var encabezado = new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion, HorasCargue = 4m, HorasDescargue = 6m };

        var conPropias = clsSolicitudCosteo.Desde(encabezado, Linea() with { HorasCargue = 1m });
        var sinPropias = clsSolicitudCosteo.Desde(encabezado, Linea());

        (conPropias.HorasCargue, conPropias.HorasDescargue).Should().Be(((decimal?)1m, (decimal?)6m));
        (sinPropias.HorasCargue, sinPropias.HorasDescargue).Should().Be(((decimal?)4m, (decimal?)6m));
    }
}
