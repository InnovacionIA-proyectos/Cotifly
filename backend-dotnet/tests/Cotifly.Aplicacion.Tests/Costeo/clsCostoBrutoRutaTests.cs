using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-039.1 (#218): <c>RutaCosteo.costo_final</c>, el costo bruto de la ruta (fletes + retorno + levante + conexos, sin margen) que la
/// vista del costeo expone al dueño. Siempre en COP y <c>null</c> en los mismos casos que <c>precio_final_linea</c>. Reversa de la regla
/// de ADR-015 §5 confirmada por la Gerencia Comercial el 2026-10-27.
/// </summary>
public sealed class clsCostoBrutoRutaTests
{
    [Fact]
    public void ConElCosteoListoLaRutaTraeSuCostoBrutoJuntoASuPrecio()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, retorno: 150_000m);

        var ruta = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        ruta.CostoFinal.Should().Be(2_700_000m, "flete + retorno, sin margen");
        ruta.CostoFinal.Should().Be(cotizacion.PrimeraRuta!.CostoFinal);
        ruta.PrecioFinalLinea.Should().Be(3_176_471m);
    }

    [Fact]
    public void ElCostoBrutoIncluyeLosConexosDeLaRutaSinMargen()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(clsCotizacionMuestra.Costeada(flete: 2_550_000m), "CX-1", "Cargue", 85_000m, 2m);

        var ruta = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        ruta.CostoFinal.Should().Be(2_720_000m, "2.550.000 de flete + 2 × 85.000 de conexo");
    }

    [Fact]
    public void SinCosteoDeNexoLaRutaNoTraeCostoNiPrecio()
    {
        var ruta = clsCosteoCotizacionService.Vista(clsCotizacionMuestra.Costeada(flete: null), null).Rutas.Single();

        ruta.CostoFinal.Should().BeNull();
        ruta.PrecioFinalLinea.Should().BeNull();
    }

    [Fact]
    public void ConCostoPeroSinPrecioLaRutaNoMuestraElCosto()
    {
        var cotizacion = clsCotizacionMuestra.Chat(costo: 2_000_000m, precio: null);
        cotizacion.PrimeraRuta!.CostoFinal.Should().Be(2_000_000m, "el costo está guardado");

        var ruta = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        ruta.PrecioFinalLinea.Should().BeNull();
        ruta.CostoFinal.Should().BeNull("costo_final sale en los mismos casos que precio_final_linea");
    }

    [Fact]
    public void EnUnaCotizacionEnDolaresElCostoBrutoDeCadaRutaSigueEnPesos()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_234_567m, 2_345_678m], moneda: "USD", trm: 4_000m);

        var vista = clsCosteoCotizacionService.Vista(cotizacion, null);

        vista.Moneda.Should().Be("USD");
        vista.Rutas.Select(r => r.CostoFinal).Should().Equal(1_234_567m, 2_345_678m);
        vista.Rutas.Select(r => r.PrecioFinalLinea).Should().OnlyContain(p => p < 1_000m, "el precio sí va en dólares");
    }

    [Fact]
    public void SinCostoFinalLaVistaDelGerenteQuitaElCostoYDejaTodoLoDemasIgual()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m]);
        var vista = clsCosteoCotizacionService.Vista(cotizacion, null);

        var sinCosto = vista.SinCostoFinal();

        sinCosto.Rutas.Should().OnlyContain(r => r.CostoFinal == null);
        sinCosto.Should().BeEquivalentTo(vista, o => o.Excluding(v => v.Rutas));
        sinCosto.Rutas.Should().BeEquivalentTo(vista.Rutas, o => o.Excluding(r => r.CostoFinal));
        vista.Rutas.Should().OnlyContain(r => r.CostoFinal != null, "la vista del dueño no cambia");
    }
    /// <summary>
    /// Validador (#218): el costo bruto que ve el dueño es exactamente el costo del que sale el precio de la ruta (también en USD con la
    /// TRM fijada), y la suma de las rutas es el costo interno de la cotización. HU-043.2 (#251): el margen se aplica solo al transporte
    /// (costo bruto − costo de los conexos) y los conexos suman su venta sin margen.
    /// </summary>
    [Theory]
    [InlineData("COP", 0)]
    [InlineData("USD", 4000)]
    public void ElCostoBrutoEsElCostoDelQueSaleElPrecioDeCadaRuta(string moneda, int trm)
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(
            clsCotizacionMuestra.LicitacionCosteada([1_234_567m, 2_345_678m, 987_654m], margen: 0.2m, moneda: moneda, trm: trm),
            "CX-9", "Escolta", 123_456m, 3m, orden: 2);

        var vista = clsCosteoCotizacionService.Vista(cotizacion, null);

        vista.Rutas.Select(r => r.CostoFinal).Should().Equal(1_234_567m, 2_345_678m + 3 * 123_456m, 987_654m);
        foreach (var ruta in vista.Rutas)
        {
            var dominio = cotizacion.RutasOrdenadas.Single(r => r.Orden == ruta.LineaOrden);
            clsLineaSolicitud.PrecioCon(ruta.CostoFinal - dominio.CostoServiciosConexos, ruta.MargenAplicado, "margen", moneda, trm, out var precio)
                .Should().BeNull();
            var ventas = dominio.Conexos.Sum(c => clsLineaSolicitud.VentaConexo(c, moneda, trm, out var venta) is null ? venta!.Value : 0m);
            (precio + ventas).Should().Be(ruta.PrecioFinalLinea, $"la ruta {ruta.LineaOrden} cobra su transporte con su margen y sus conexos a su venta");
        }

        vista.Rutas.Sum(r => r.CostoFinal).Should().Be(cotizacion.CostoFinal, "la suma de las rutas es el costo de la cotización");
    }

    /// <summary>Validador (#218): el costo bruto no cambia con el margen; solo cambia el precio.</summary>
    [Fact]
    public void AjustarElMargenNoCambiaElCostoBrutoSoloElPrecio()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m);
        var antes = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.25m, OrigenMargen.Negociado, "margen");
        cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora);
        var despues = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();

        (antes.CostoFinal, antes.PrecioFinalLinea).Should().Be(((decimal?)2_550_000m, (decimal?)3_000_000m));
        (despues.CostoFinal, despues.PrecioFinalLinea).Should().Be(((decimal?)2_550_000m, (decimal?)3_400_000m));
    }
}
