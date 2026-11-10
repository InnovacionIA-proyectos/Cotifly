using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Tests.Costeo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// HU-045.3 (#265, ADR-032 §9): los datos del Excel de la cotización y de la exportación de la licitación traen el seguro de la mercancía por
/// ruta, aparte del transporte y de los conexos, y el desglose cuadra: transporte + conexos + seguro = precio de la ruta y total. Es venta,
/// así que lo conserva también la versión del gerente.
/// </summary>
public sealed class clsSeguroExportacionesTests
{
    private const decimal Tasa = 0.003450m;

    private static clsCotizacionSolicitud ConSeguro(string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, moneda: moneda, trm: trm,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 200_000_000m });
        cotizacion.CambiarSeguro(1, true, Tasa, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        return cotizacion;
    }

    [Theory]
    [InlineData(clsCotizacionSolicitud.MonedaPesos, 0, 690_000, 1_250_000)]
    [InlineData("USD", 4_000, 172.50, 312.50)]
    public void ElExcelDeLaCotizacionTraeElSeguroAparteYElDesgloseCuadra(string moneda, decimal trm, decimal seguro, decimal transporte)
    {
        var datos = clsExportacionCotizacionService.Datos(ConSeguro(moneda, trm), "Ana", clsCotizacionMuestra.Ahora);

        var ruta = datos.Rutas.Single();
        ruta.ValorSeguro.Should().Be(seguro);
        ruta.SeguroSinValor.Should().BeFalse();
        ruta.PrecioTransporte.Should().Be(transporte, "la tarifa del transporte no absorbe el seguro");
        (ruta.PrecioTransporte + ruta.PrecioConexos + ruta.ValorSeguro).Should().Be(ruta.PrecioRuta);
        datos.Totales.PrecioSeguro.Should().Be(seguro);
        (datos.Totales.PrecioTransporte + datos.Totales.PrecioConexos + datos.Totales.PrecioSeguro).Should().Be(datos.Totales.PrecioFinal);
        datos.SinCostos().Rutas.Single().ValorSeguro.Should().Be(seguro, "el seguro es venta, no costo: lo ve el gerente");
        datos.SinCostos().Totales.PrecioSeguro.Should().Be(seguro);
    }

    [Fact]
    public void SinSeguroElTotalDelSeguroEsCero()
    {
        var datos = clsExportacionCotizacionService.Datos(clsCotizacionMuestra.Costeada(), "Ana", clsCotizacionMuestra.Ahora);

        datos.Rutas.Single().ValorSeguro.Should().BeNull();
        datos.Totales.PrecioSeguro.Should().Be(0m);
    }

    [Fact]
    public void ConElSeguroMarcadoSinValorLaRutaLoDiceYNoSuma()
    {
        var cotizacion = ConSeguro();
        clsSeguroPdfCosteoTests.QuitarValor(cotizacion);

        var datos = clsExportacionCotizacionService.Datos(cotizacion, "Ana", clsCotizacionMuestra.Ahora);

        datos.Rutas.Single().Should().Match<Cotifly.Aplicacion.Cotizaciones.DTOs.clsExcelRuta>(r => r.SeguroSinValor && r.ValorSeguro == null);
        datos.Totales.PrecioSeguro.Should().Be(0m);
    }

    [Fact]
    public void LaExportacionDeLaLicitacionLlevaElSeguroDeCadaLinea()
    {
        var cotizacion = ConSeguro();

        var datos = clsLicitacionService.DatosExportacion(cotizacion, null, clsCotizacionMuestra.Ahora);

        var linea = datos.Lineas.Single();
        linea.ValorSeguro.Should().Be(690_000m);
        linea.PrecioFinal.Should().Be(1_250_000m, "la tarifa del transporte, sin el seguro ni los conexos");
    }
}
