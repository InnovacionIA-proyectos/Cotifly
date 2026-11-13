using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// La vista del costeo con el seguro de la mercancía (<c>incluye_seguro</c>, <c>tasa_seguro</c>, <c>valor_seguro</c> en la
/// moneda de la cotización) y la tarifa del transporte, que no lo absorbe (va aparte, como los conexos).
/// </summary>
public sealed class clsSeguroVistaCosteoTests
{
    private const decimal Tasa = 0.003450m;

    private static clsCotizacionSolicitud ConSeguro(string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, moneda: moneda, trm: trm,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 200_000_000m });
        cotizacion.CambiarSeguro(1, true, Tasa, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        return cotizacion;
    }

    [Fact]
    public void LaRutaDelCosteoTraeElCheckLaTasaYElValorDelSeguro()
    {
        var ruta = clsCosteoCotizacionService.Vista(ConSeguro(), null).Rutas.Single();

        ruta.IncluyeSeguro.Should().BeTrue();
        ruta.TasaSeguro.Should().Be(Tasa);
        ruta.ValorSeguro.Should().Be(690_000m);
        ruta.PrecioFinalLinea.Should().Be(1_940_000m);
        ruta.CostoFinal.Should().Be(1_000_000m, "el seguro no es costo de Nexo");
        // El costo del transporte (solo el flete) y el valor de la mercancía salen en la misma vista; el seguro no los toca.
        ruta.CostoTransporte.Should().Be(1_000_000m, "el seguro no entra en el costo del transporte");
        ruta.ValorMercancia.Should().Be(200_000_000m);
    }

    [Fact]
    public void EnDolaresElValorDeLaMercanciaYElCostoDelTransporteSiguenEnPesos()
    {
        var ruta = clsCosteoCotizacionService.Vista(ConSeguro("USD", 4_000m), null).Rutas.Single();

        ruta.ValorMercancia.Should().Be(200_000_000m, "el valor de la mercancía va en COP aunque la cotización esté en USD");
        ruta.CostoTransporte.Should().Be(1_000_000m, "el costo del transporte va en COP aunque la cotización esté en USD");
    }

    [Fact]
    public void EnDolaresElValorDelSeguroVieneConvertidoConLaTrm()
    {
        var vista = clsCosteoCotizacionService.Vista(ConSeguro("USD", 4_000m), null);

        vista.Rutas.Single().ValorSeguro.Should().Be(172.50m, "690.000 / 4.000");
        vista.PrecioFinal.Should().Be(485.00m, "1.250.000 / 4.000 + 172,50");
    }

    [Fact]
    public void SinSeguroLosCamposVienenVaciosYLaTasaVigenteSoloLaPoneElGet()
    {
        var vista = clsCosteoCotizacionService.Vista(clsCotizacionMuestra.Costeada(), null);

        vista.Rutas.Single().Should().Match<clsRutaCosteo>(r => !r.IncluyeSeguro && r.TasaSeguro == null && r.ValorSeguro == null);
        vista.TasaSeguroVigente.Should().BeNull("la vista compartida no la lee; GET .../costeo y PUT .../costeo/seguro sí");
    }

    [Fact]
    public void LaTarifaDelTransporteNoAbsorbeElSeguro()
    {
        var cotizacion = ConSeguro();
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 1m, PrecioVenta: 70_000m)], clsCotizacionMuestra.Ahora);

        clsCosteoCotizacionService.PrecioTransporte(cotizacion, cotizacion.PrimeraRuta!).Should().Be(1_250_000m,
            "1.000.000 / 0,8: el seguro (690.000) y el conexo (70.000) van aparte");
        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(1_250_000m + 70_000m + 690_000m);
    }
}
