using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-045.2 (#264), validación adversarial de la vista: la tarifa del transporte no absorbe el seguro ni en dólares ni sin conexos (la
/// tolerancia por conexo es 0 en ese caso), y la ruta con el check pero sin valor no inventa un seguro.
/// </summary>
public sealed class clsSeguroVistaCosteoAdversarialTests
{
    private const decimal Tasa = 0.003450m;

    private static clsCotizacionSolicitud Costeada(decimal? valor, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m) =>
        clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, moneda: moneda, trm: trm,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor });

    [Theory]
    [InlineData("COP", "0", "1250000")]
    [InlineData("USD", "4000", "312.50")]
    [InlineData("USD", "3999.99", "312.50")]
    [InlineData("USD", "3333.33", "375.00")]
    public void SinConexosLaTarifaDelTransporteMasElSeguroDaExactamenteElPrecioDeLaRuta(string moneda, string trm, string transporte)
    {
        var t = decimal.Parse(trm, System.Globalization.CultureInfo.InvariantCulture);
        var cotizacion = Costeada(200_000_290m, moneda, t);
        cotizacion.CambiarSeguro(1, true, Tasa, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        var ruta = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        var tarifa = clsCosteoCotizacionService.PrecioTransporte(cotizacion, cotizacion.PrimeraRuta!);

        tarifa.Should().Be(decimal.Parse(transporte, System.Globalization.CultureInfo.InvariantCulture));
        (tarifa + ruta.ValorSeguro).Should().Be(ruta.PrecioFinalLinea, "transporte + seguro = precio de la ruta, sin diferencia de redondeo");
    }

    [Fact]
    public void ConElCheckPeroSinValorLaVistaNoTraeTasaNiValorYElPrecioEsElDelTransporte()
    {
        var cotizacion = Costeada(200_000_000m);
        cotizacion.CambiarSeguro(1, true, Tasa, clsCotizacionMuestra.Ahora);
        var edicion = Edicion(cotizacion.PrimeraRuta!);
        cotizacion.AplicarEdicion(edicion, clsCotizacionMuestra.Ahora);

        var ruta = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        ruta.Should().Match<clsRutaCosteo>(r => r.IncluyeSeguro && r.TasaSeguro == null && r.ValorSeguro == null);
    }

    [Fact]
    public void LaVistaNuncaExponeElCostoSeguroComoCampoPropio()
    {
        var nombres = typeof(clsRutaCosteo).GetProperties().Select(p => p.Name).ToList();

        nombres.Should().Contain("ValorSeguro").And.NotContain("CostoSeguro", "decisión del usuario: el campo expuesto es valor_seguro");
        typeof(clsCosteoCotizacion).GetProperties().Select(p => p.Name).Should().NotContain("CostoSeguro");
    }

    private static clsEdicionSolicitud Edicion(clsLineaSolicitud ruta)
    {
        var linea = clsSolicitudCosteoMuestra.RutaCaptura() with { ValorMercancia = null };
        var capturada = new clsRutaCapturada(linea, 3, 0, [new clsCamionPlaneado(null, null, linea.PesoKg)], []);
        return new clsEdicionSolicitud(
            new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline },
            [new clsRutaEditada(ruta.Id, capturada, Replanificada: false)])
        {
            TasaSeguroVigente = Tasa,
        };
    }
}
