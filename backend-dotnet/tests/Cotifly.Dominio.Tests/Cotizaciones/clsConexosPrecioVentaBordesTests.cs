using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Bordes del precio de la ruta con los conexos a su venta, sin margen (venta 0, margen 0, USD sin TRM, redondeo de
/// varias ventas, la heurística de la fórmula anterior, varias rutas y una lectura sin venta).
/// </summary>
public sealed class clsConexosPrecioVentaBordesTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) Ruta(
        decimal flete = 1_900_000m, decimal margen = 0.20m, string metodo = "margen", string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m)
    {
        var cotizacion = clsCotizacionMuestra.Formulario(moneda: moneda, trm: trm);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(margen, margen, OrigenMargen.PoliticaLinea, metodo);
        ruta.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, flete, 0m);
        cotizacion.RecalcularTotales(_ahora);
        return (cotizacion, ruta);
    }

    [Fact]
    public void VentaCeroNoSumaYElConexoNoDesaparece()
    {
        var (cotizacion, ruta) = Ruta();
        var transporte = ruta.PrecioFinalLinea;

        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-0", "Gratis", 50_000m, 3m, PrecioVenta: 0m)], _ahora).EsExitoso.Should().BeTrue();

        ruta.Conexos.Should().ContainSingle().Which.PrecioVenta.Should().Be(0m);
        ruta.PrecioFinalLinea.Should().Be(transporte, "venta 0: el conexo no suma al precio");
        ruta.CostoFinal.Should().Be(1_900_000m + 150_000m, "el costo sí lo incluye");
    }

    [Fact]
    public void ConMargenCeroElPrecioEsTransporteMasVenta()
    {
        var (cotizacion, ruta) = Ruta(margen: 0m);

        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 2m, PrecioVenta: 70_000m)], _ahora).EsExitoso.Should().BeTrue();

        ruta.PrecioFinalLinea.Should().Be(1_900_000m + 140_000m);
    }

    [Fact]
    public void EnDolaresSinTrmLaRutaQuedaSinPrecioYNoSeCaeLaVentaSola()
    {
        // Una solicitud en USD sin TRM no se puede crear (COTIZACION_INVALIDA); además la venta sola no se convierte sin TRM.
        FluentActions.Invoking(() => Ruta(moneda: "USD", trm: 0m)).Should().Throw<InvalidOperationException>();
        var (cotizacion, ruta) = Ruta(moneda: "USD", trm: 4_000m);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 2m, PrecioVenta: 70_000m)], _ahora);

        clsLineaSolicitud.VentaConexo(ruta.Conexos.Single(), "USD", 0m, out var venta).Should().NotBeNull("sin TRM la conversión es un error");
        venta.Should().BeNull();
        ruta.PrecioRutaCon(0.20m, "margen", "USD", 0m, out var precio).Should().NotBeNull();
        precio.Should().BeNull("sin TRM no hay precio en dólares");
    }

    [Theory]
    [InlineData("COP", 0.0)]
    [InlineData("USD", 3917.37)]
    [InlineData("USD", 4123.99)]
    public void ElPrecioEsExactamenteTransporteMasCadaVentaRedondeada(string moneda, double trm)
    {
        var (cotizacion, ruta) = Ruta(flete: 1_234_567m, margen: 0.173m, moneda: moneda, trm: (decimal)trm);
        cotizacion.CambiarConexos(1,
        [
            new clsCambioConexo("CX-1", "A", 10_000.33m, 1.5m, PrecioVenta: 12_345.67m),
            new clsCambioConexo("CX-2", "B", 999.99m, 3m, PrecioVenta: 1_000.50m),
            new clsCambioConexo("CX-3", "C", 1m, 0.33m, PrecioVenta: 33_333.33m),
        ], _ahora).EsExitoso.Should().BeTrue();

        ruta.PrecioDe(ruta.CostoTransporte, cotizacion.Moneda, cotizacion.TrmAplicada, out var transporte).Should().BeNull();
        var ventas = ruta.Conexos.Select(c =>
        {
            clsLineaSolicitud.VentaConexo(c, cotizacion.Moneda, cotizacion.TrmAplicada, out var v).Should().BeNull();
            return v!.Value;
        }).ToList();

        ruta.PrecioFinalLinea.Should().Be(transporte!.Value + ventas.Sum(), "sin doble conteo ni margen sobre conexos");
        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeFalse("una cotización nueva no es de la fórmula anterior");
    }

    [Fact]
    public void CuandoLaVentaHaceCoincidirLasDosFormulasLaHeuristicaNoLaConfundeConUnaVieja()
    {
        // (1.900.000 + 80.000) / 0,8 = 2.475.000 = 1.900.000 / 0,8 + 100.000
        var (cotizacion, ruta) = Ruta();
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 80_000m, 1m, PrecioVenta: 100_000m)], _ahora);

        ruta.PrecioFinalLinea.Should().Be(2_475_000m);
        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeFalse();
    }

    [Fact]
    public void VariasRutasElTotalEsLaSumaDeCadaRutaConSusVentas()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], margen: 0.20m, tipo: TipoSolicitud.Spot);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "A", 10_000m, 1m, PrecioVenta: 15_000m)], _ahora).EsExitoso.Should().BeTrue();
        cotizacion.CambiarConexos(2, [new clsCambioConexo("CX-2", "B", 20_000m, 2m, PrecioVenta: 25_000m)], _ahora).EsExitoso.Should().BeTrue();

        var rutas = cotizacion.RutasOrdenadas;
        rutas[0].PrecioFinalLinea.Should().Be(1_250_000m + 15_000m);
        rutas[1].PrecioFinalLinea.Should().Be(2_500_000m + 50_000m);
        cotizacion.PrecioFinal.Should().Be(rutas.Sum(r => r.PrecioFinalLinea!.Value));
        cotizacion.CostoFinal.Should().Be(1_000_000m + 10_000m + 2_000_000m + 40_000m);
    }

    [Fact]
    public void UnaLecturaSinVentaConservaLaVentaConocida()
    {
        // Un Nexo sin valorVenta que vuelve a leer el conexo no devuelve al costo una venta ya conocida.
        var (cotizacion, ruta) = Ruta();
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 1m, PrecioVenta: 70_000m)], _ahora);

        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 55_000m, 1m)], _ahora).Valor.Should().Be(1, "cambió el costo");

        ruta.Conexos.Single().Should().Match<clsServicioConexoRuta>(c => c.Precio == 55_000m && c.PrecioVenta == 70_000m);
        ruta.PrecioFinalLinea.Should().Be(2_375_000m + 70_000m);
    }

    [Fact]
    public void SinVentaConocidaElConexoSigueAlCostoNuevo()
    {
        // Venta por defecto (= costo): sin venta en la lectura, sigue al costo nuevo.
        var (cotizacion, ruta) = Ruta();
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 1m)], _ahora);

        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 55_000m, 1m)], _ahora);

        ruta.Conexos.Single().PrecioVenta.Should().Be(55_000m);
    }

    [Fact]
    public void LaVentaSeGuardaADosDecimalesHalfToEven()
    {
        var (cotizacion, ruta) = Ruta();

        cotizacion.CambiarConexos(1,
        [
            new clsCambioConexo("CX-1", "A", 1m, 1m, PrecioVenta: 12_345.675m),
            new clsCambioConexo("CX-2", "B", 1m, 1m, PrecioVenta: 12_345.665m),
            new clsCambioConexo("CX-3", "C", 1m, 1m, PrecioVenta: 0.0049m),
        ], _ahora);

        ruta.Conexos.Select(c => c.PrecioVenta).Should().Equal(12_345.68m, 12_345.66m, 0m);
    }

    [Fact]
    public void RefrescarLaVentaCambiaSoloLaVentaYNoTocaUnaCotizacionCongelada()
    {
        var (cotizacion, ruta) = Ruta();
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 1m)], _ahora);

        cotizacion.RefrescarVentaConexos([new clsVentaConexo(ruta.Id, "cx-1", 70_000.004m), new clsVentaConexo(ruta.Id, "OTRO", 1m)])
            .Should().Be(1, "un conexo que no es de la ruta no cuenta");
        ruta.Conexos.Single().Should().Match<clsServicioConexoRuta>(c => c.Precio == 50_000m && c.PrecioVenta == 70_000m);
        cotizacion.RefrescarVentaConexos([new clsVentaConexo(ruta.Id, "CX-1", 70_000m)]).Should().Be(0, "la misma venta no cambia nada");

        cotizacion.AsignarPdf("PCN-X.pdf");
        cotizacion.RefrescarVentaConexos([new clsVentaConexo(ruta.Id, "CX-1", 90_000m)]).Should().Be(0, "con el precio congelado no se toca");
        ruta.Conexos.Single().PrecioVenta.Should().Be(70_000m);
    }
}
