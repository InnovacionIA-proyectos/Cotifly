using System.Text.Json;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Conexos a su precio de venta: desglose (transporte + conexos), heurística de la fórmula anterior, PDF sin costo, vista previa del
/// margen e importación de Nexo con datos raros.
/// </summary>
public sealed class clsConexosPrecioVentaAplicacionTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    [Theory]
    [InlineData("COP", 0.0)]
    [InlineData("USD", 3917.37)]
    public void ElDesgloseCuadraExactoConElPrecioDeLaRuta(string moneda, double trm)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_234_567m, margen: 0.173m, moneda: moneda, trm: (decimal)trm);
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.CambiarConexos(1,
        [
            new clsCambioConexo("CX-1", "A", 10_000.33m, 1.5m, PrecioVenta: 12_345.67m),
            new clsCambioConexo("CX-2", "B", 999.99m, 3m, PrecioVenta: 1_000.50m),
            new clsCambioConexo("CX-3", "C", 1m, 0.33m, PrecioVenta: 33_333.33m),
        ], _ahora).EsExitoso.Should().BeTrue();

        var conexos = clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta);
        var transporte = clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta);

        conexos.Should().HaveCount(3);
        (transporte + conexos.Sum(c => c.Precio)).Should().Be(ruta.PrecioFinalLinea);
        ruta.PrecioDe(ruta.CostoTransporte, cotizacion.Moneda, cotizacion.TrmAplicada, out var calculado).Should().BeNull();
        transporte.Should().Be(calculado, "la tarifa del transporte es la calculada, sin absorber diferencias");
    }

    [Fact]
    public void UnConexoConVentaCeroSaleEnElDesgloseYEnElPdfConPrecioCero()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-0", "Cortesía", 40_000m, 1m, PrecioVenta: 0m)], _ahora);

        clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Should().ContainSingle().Which.Precio.Should().Be(0m);
        clsCosteoCotizacionService.Payload(cotizacion, null, "COP").ServiciosConexos.Should().ContainSingle(s => s.Descripcion == "Cortesía");
        ruta.PrecioFinalLinea.Should().Be(3_000_000m);
    }

    [Fact]
    public void ElPdfNoLlevaElCostoDelConexoNiElCostoFinal()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 123_457m, 1m, PrecioVenta: 150_001m)], _ahora);

        var json = JsonSerializer.Serialize(clsCosteoCotizacionService.Payload(cotizacion, "Contenedores", "COP"));
        var pliego = JsonSerializer.Serialize(clsCosteoCotizacionService.PayloadPliego(cotizacion, "Contenedores", "COP"));

        foreach (var texto in new[] { json, pliego })
        {
            texto.Should().NotContain("123457").And.NotContain("123.457").And.NotContain("2673457", "ni el costo del conexo ni el costo final");
            texto.Should().Contain("3150001", "la tarifa es el precio final: 3.000.000 + 150.001");
        }
    }

    [Fact]
    public void LaVistaPreviaConElMismoMargenDaElPrecioActualDeUnaCotizacionNueva()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 85_000m, 2m, PrecioVenta: 100_000m)], _ahora);

        var previa = clsAjusteMargenCosteoService.Recalcular(cotizacion, ruta, 0.15m);

        previa.EsExitoso.Should().BeTrue();
        previa.Valor.PrecioRuta.Should().Be(ruta.PrecioFinalLinea!.Value);
        previa.Valor.PrecioFinal.Should().Be(cotizacion.PrecioFinal);
        clsAjusteMargenCosteoService.Recalcular(cotizacion, ruta, 0.25m).Valor.PrecioRuta.Should().Be(3_400_000m + 200_000m);
    }

    [Fact]
    public void FinalizadaEnDolaresConLaFormulaAnteriorYVariosConexosElDesgloseSigueCuadrando()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, moneda: "USD", trm: 3917.37m, usuarioId: Guid.NewGuid());
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.CambiarConexos(1,
        [
            new clsCambioConexo("CX-1", "A", 85_000m, 1m),
            new clsCambioConexo("CX-2", "B", 12_345.67m, 3m),
        ], _ahora);
        ruta.PrecioDe(ruta.CostoFinal, cotizacion.Moneda, cotizacion.TrmAplicada, out var anterior).Should().BeNull();
        ruta.FijarPrecio(anterior);
        cotizacion.RecalcularPrecio(_ahora);

        ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada).Should().BeTrue();
        var conexos = clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta);
        (clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta) + conexos.Sum(c => c.Precio)).Should().Be(ruta.PrecioFinalLinea);
    }

    [Theory]
    [InlineData(0.0, null, 500_000.0, 500_000.0)]        // cantidad 0 → 1; venta = valorVenta / 1
    [InlineData(2.0, 0.0, 999.0, 0.0)]                  // precioVentaUnitario 0 se respeta
    [InlineData(3.0, null, 100_000.0, 33_333.33)]        // valorVenta / cantidad, 2 decimales
    public void ImportarConexoConCantidadesYVentasRaras(double cantidad, double? unitaria, double? valorVenta, double esperada)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        var ruta = cotizacion.PrimeraRuta!;
        var conexo = new clsConexoNexo("CX-9", "Raro", "logistico", (decimal)cantidad, 300_000m)
        {
            PrecioVentaUnitario = (decimal?)unitaria, ValorVenta = (decimal?)valorVenta,
        };

        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta, [conexo], _ahora).EsExitoso.Should().BeTrue();

        var fila = ruta.Conexos.Should().ContainSingle().Subject;
        fila.PrecioVenta.Should().Be((decimal)esperada);
        ruta.CostoServiciosConexos.Should().Be(fila.Precio * fila.Cantidad);
        clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Single().Precio
            .Should().Be(Math.Round(fila.Venta, 0, MidpointRounding.ToEven), "el comercial ve la venta (sin margen), no el costo");
    }

    [Theory]
    [InlineData(null, 1_000.0)]
    [InlineData(-5.0, 1_000.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(1_500.0, 1_500.0)]
    public void LaVentaDelCatalogoUsaPrecioVentaOElCosto(double? precioVenta, double esperada)
    {
        new clsCostoConexoNexo("S-1", "Servicio", 1_000m, "logistico", (decimal?)precioVenta).VentaUnitaria.Should().Be((decimal)esperada);
    }

    // ------------------------------------------------------------------ Venta de Nexo para los conexos ya guardados (polling)

    private static bool Leer(clsCotizacionSolicitud cotizacion, params clsConexoNexo[] conexos) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, clsCotizacionMuestra.Foto([2_550_000m], conexos: conexos), _ahora.AddMinutes(5),
            new clsOpcionesCosteoNexo(), new clsPrecioCosteoService());

    [Fact]
    public void ElPollingRefrescaLaVentaDeUnConexoGuardadoAlCostoPorSuIdDeNexo()
    {
        // Un conexo importado antes del despliegue: su fila es la de Nexo (id CX-1) y quedó con precioVenta = precio.
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, 2m);
        cotizacion.PrecioFinal.Should().Be(3_170_000m);

        Leer(cotizacion, new clsConexoNexo("CX-1", "Cargue", "logistico", 2m, 170_000m) { PrecioVentaUnitario = 100_000m, ValorVenta = 200_000m })
            .Should().BeTrue();

        cotizacion.PrimeraRuta!.Conexos.Single().Should().Match<clsServicioConexoRuta>(c => c.Precio == 85_000m && c.PrecioVenta == 100_000m);
        cotizacion.PrecioFinal.Should().Be(3_200_000m, "2.550.000 / 0,85 + 200.000: la venta, no el costo");
        cotizacion.CostoFinal.Should().Be(2_720_000m, "el costo no cambia");
        Leer(cotizacion, new clsConexoNexo("CX-1", "Cargue", "logistico", 2m, 170_000m) { PrecioVentaUnitario = 100_000m })
            .Should().BeFalse("la misma venta en la siguiente vuelta no es un cambio");
    }

    [Fact]
    public void UnConexoCapturadoSeCruzaPorElServicioDelCatalogoYSuRuta()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("SRV-1", "Cargue", 85_000m, 1m, "SRV-1")], _ahora);

        Leer(cotizacion, new clsConexoNexo("FILA-NX", "Cargue", "logistico", 1m, 85_000m)
        {
            ServicioConexoId = "SRV-1", CotizacionLineaId = "CL1", PrecioVentaUnitario = 120_000m,
        });

        cotizacion.PrimeraRuta!.Conexos.Single().PrecioVenta.Should().Be(120_000m);
        cotizacion.PrecioFinal.Should().Be(3_120_000m);
    }

    [Fact]
    public void SinVentaEnNexoOConDosVentasDistintasElConexoConservaLaSuya()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("SRV-1", "Cargue", 85_000m, 1m, "SRV-1", PrecioVenta: 110_000m)], _ahora);

        Leer(cotizacion, new clsConexoNexo("FILA-1", "Cargue", "logistico", 1m, 85_000m) { ServicioConexoId = "SRV-1", CotizacionLineaId = "CL1" });
        cotizacion.PrimeraRuta!.Conexos.Single().PrecioVenta.Should().Be(110_000m, "Nexo sin venta no la devuelve al costo");

        Leer(cotizacion,
            new clsConexoNexo("FILA-1", "Cargue", "logistico", 1m, 85_000m) { ServicioConexoId = "SRV-1", CotizacionLineaId = "CL1", PrecioVentaUnitario = 120_000m },
            new clsConexoNexo("FILA-2", "Cargue", "logistico", 1m, 85_000m) { ServicioConexoId = "SRV-1", CotizacionLineaId = "CL1", PrecioVentaUnitario = 130_000m });
        cotizacion.PrimeraRuta.Conexos.Single().PrecioVenta.Should().Be(110_000m, "dos ventas distintas para el mismo servicio: no se adivina");
    }

    [Fact]
    public void ConElPrecioCongeladoElPollingNoRefrescaLaVenta()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        cotizacion.AsignarPdf("PCN-X.pdf");
        var precio = cotizacion.PrecioFinal;

        Leer(cotizacion, new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 85_000m) { PrecioVentaUnitario = 100_000m });

        cotizacion.PrimeraRuta!.Conexos.Single().PrecioVenta.Should().Be(85_000m);
        cotizacion.PrecioFinal.Should().Be(precio);
    }
}
