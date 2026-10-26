using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
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
}
