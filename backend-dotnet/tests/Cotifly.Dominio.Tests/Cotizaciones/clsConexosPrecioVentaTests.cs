using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Los servicios conexos se cotizan a su precio de VENTA (<c>precioVenta</c>, script 19), sin margen. El precio de la
/// ruta es (fletes + retorno + levante) con el margen de la ruta + la venta de sus conexos; el costo (<c>CostoFinal</c>,
/// <c>CostoServiciosConexos</c>) sigue siendo el costo real de Nexo.
/// </summary>
public sealed class clsConexosPrecioVentaTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    /// <summary>
    /// Ruta de un camión con flete 1.500.000, retorno 200.000 y levante 200.000 (transporte 1.900.000), margen 20 % (método
    /// <c>margen</c>) y el conexo CX-1: costo 50.000 y venta 70.000 por unidad, 2 unidades.
    /// </summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) RutaConConexo(
        string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m, decimal? ventaUnitaria = 70_000m)
    {
        var cotizacion = clsCotizacionMuestra.Formulario(moneda: moneda, trm: trm, solicitud: clsSolicitudCosteoMuestra.Crear() with
        {
            LevanteContenedorDiferente = true, CiudadLevanteContenedor = "Cartagena", ValorAprovisionamiento = 200_000m,
        });
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(0.15m, 0.20m, OrigenMargen.PoliticaLinea, "margen");
        ruta.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_500_000m, 200_000m);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 2m, PrecioVenta: ventaUnitaria)], _ahora).EsExitoso.Should().BeTrue();
        return (cotizacion, ruta);
    }

    [Fact]
    public void ElPrecioDeLaRutaEsElTransporteConMargenMasLaVentaDeLosConexosSinMargen()
    {
        var (cotizacion, ruta) = RutaConConexo();

        ruta.CostoTransporte.Should().Be(1_900_000m);
        ruta.CostoServiciosConexos.Should().Be(100_000m, "el costo sigue siendo precio (costo unitario) × cantidad");
        ruta.CostoFinal.Should().Be(2_000_000m, "costo_final (HU-039.1) no cambia: es el costo real");
        ruta.Conexos.Sum(c => c.Venta).Should().Be(140_000m, "precioVenta × cantidad");
        ruta.PrecioFinalLinea.Should().Be(2_515_000m, "1.900.000 / (1 − 0,20) = 2.375.000, + 140.000 de conexos sin margen");
        cotizacion.PrecioFinal.Should().Be(2_515_000m);
        cotizacion.CostoFinal.Should().Be(2_000_000m);
    }

    [Fact]
    public void SinPrecioDeVentaElConexoSeCotizaAlCostoYSinMargen()
    {
        var (_, ruta) = RutaConConexo(ventaUnitaria: null);

        ruta.Conexos.Single().PrecioVenta.Should().Be(50_000m, "sin venta, Nexo (y Cotifly) cotizan el conexo al costo");
        ruta.PrecioFinalLinea.Should().Be(2_475_000m, "2.375.000 + 100.000: el conexo no lleva margen aunque se cotice al costo");
    }

    [Fact]
    public void EnDolaresElTransporteYLaVentaSeConviertenConLaTrmDeLaSolicitud()
    {
        var (cotizacion, ruta) = RutaConConexo("USD", 4_000m);

        ruta.PrecioFinalLinea.Should().Be(628.75m, "2.375.000 / 4.000 = 593,75 + 140.000 / 4.000 = 35,00");
        cotizacion.PrecioFinal.Should().Be(628.75m);
        clsLineaSolicitud.VentaConexo(ruta.Conexos.Single(), cotizacion.Moneda, cotizacion.TrmAplicada, out var venta).Should().BeNull();
        venta.Should().Be(35m);
    }

    [Fact]
    public void LaVentaDeCadaConexoSeRedondeaALaUnidadDeLaMoneda()
    {
        var (cotizacion, ruta) = RutaConConexo(ventaUnitaria: 12_345.67m);

        clsLineaSolicitud.VentaConexo(ruta.Conexos.Single(), cotizacion.Moneda, cotizacion.TrmAplicada, out var venta).Should().BeNull();
        venta.Should().Be(24_691m, "12.345,67 × 2 = 24.691,34, a pesos enteros como todo precio en COP");
        ruta.PrecioFinalLinea.Should().Be(2_375_000m + 24_691m, "el precio de la ruta suma la venta ya redondeada: el desglose cuadra exacto");
    }

    [Fact]
    public void ElMargenNoCambiaLaVentaDeLosConexos()
    {
        var (cotizacion, ruta) = RutaConConexo();

        ruta.PrecioRutaCon(0.50m, "margen", cotizacion.Moneda, cotizacion.TrmAplicada, out var conMitad).Should().BeNull();
        conMitad.Should().Be(3_940_000m, "1.900.000 / 0,5 = 3.800.000 + 140.000");
        ruta.PrecioRutaCon(0.20m, "markup", cotizacion.Moneda, cotizacion.TrmAplicada, out var markup).Should().BeNull();
        markup.Should().Be(2_420_000m, "1.900.000 × 1,2 = 2.280.000 + 140.000");
        ruta.PrecioFinalLinea.Should().Be(2_515_000m, "la vista previa no cambia la ruta");
    }

    [Fact]
    public void SinFleteNiMargenLaRutaNoTienePrecioAunqueTengaConexos()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 1m, PrecioVenta: 70_000m)], _ahora);

        ruta.PrecioFinalLinea.Should().BeNull("sin flete no hay precio, aunque la venta de los conexos se conozca");
        ruta.PrecioRutaCon(0.20m, "margen", cotizacion.Moneda, cotizacion.TrmAplicada, out var precio).Should().BeNull();
        precio.Should().BeNull();
    }

    [Fact]
    public void ElPrecioDeVentaSeGuardaAlCrearSeActualizaConElConexoYSeConservaAlCambiarLaCantidad()
    {
        var ruta = clsCotizacionMuestra.Formulario().PrimeraRuta!;

        ruta.AgregarOActualizarConexo(new clsCambioConexo("CX-1", "Cargue", 10_000m, 2m, PrecioVenta: 15_000m)).Should().BeTrue();
        ruta.Conexos.Single().Should().Match<clsServicioConexoRuta>(c => c.Precio == 10_000m && c.PrecioVenta == 15_000m && c.Costo == 20_000m && c.Venta == 30_000m);

        ruta.AgregarOActualizarConexo(new clsCambioConexo("CX-1", null, 10_000m, 2m, PrecioVenta: 15_000m)).Should().BeFalse("nada cambió");
        ruta.AgregarOActualizarConexo(new clsCambioConexo("CX-1", null, 10_000m, 2m, PrecioVenta: 16_000m)).Should().BeTrue("cambió solo la venta");
        ruta.Conexos.Single().PrecioVenta.Should().Be(16_000m);

        ruta.AjustarConexos(new Dictionary<string, decimal> { ["CX-1"] = 3m });
        ruta.Conexos.Single().Should().Match<clsServicioConexoRuta>(c => c.Precio == 10_000m && c.PrecioVenta == 16_000m && c.Cantidad == 3m);

        FluentActions.Invoking(() => ruta.AgregarOActualizarConexo(new clsCambioConexo("CX-2", null, 1m, 1m, PrecioVenta: -1m)))
            .Should().Throw<ArgumentOutOfRangeException>("la venta no puede ser negativa (CHECK del script 19)");
    }

    [Fact]
    public void UnPrecioGuardadoConLaFormulaAnteriorSeReconoceParaNoDescuadrarElDesglose()
    {
        // Una ruta finalizada (o con PDF) con la fórmula anterior guardó (transporte + conexos) con margen y no se recalcula: su desglose se
        // muestra como se guardó (conexos con el margen de la ruta) para que transporte + conexos dé su precio.
        var (cotizacion, ruta) = RutaConConexo(ventaUnitaria: null);
        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeFalse("el precio es el de la fórmula nueva");

        ruta.FijarPrecio(2_500_000m);   // 2.000.000 / 0,8: la fórmula anterior
        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeTrue();

        ruta.FijarPrecio(2_400_000m);
        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeFalse("no es ninguna de las dos fórmulas");
    }

    [Fact]
    public void SinConexosLaFormulaNuevaYLaAnteriorDanLoMismo()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m);
        var ruta = cotizacion.PrimeraRuta!;

        ruta.PrecioFinalLinea.Should().Be(1_250_000m);
        ruta.Conexos.Sum(c => c.Venta).Should().Be(0m);
        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeFalse("sin conexos no hay nada que distinguir");
    }
}
