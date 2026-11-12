using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Cruce de la venta de Nexo con los conexos ya guardados (polling) y la regla de una lectura sin venta.
/// </summary>
public sealed class clsRefrescoVentaConexosTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    private static clsCotizacionNexo Nexo(params clsConexoNexo[] conexos) =>
        new("COT-1", "Borrador", "SinDefinir",
            [new clsLineaCosteoNexo("L1", 1_000_000m, null, null, "CL1"), new clsLineaCosteoNexo("L2", 2_000_000m, null, null, "CL2")], conexos);

    private static clsConexoNexo Fila(string id, string servicio, string linea, decimal venta) =>
        new(id, "Cargue", "logistico", 1m, 10_000m) { ServicioConexoId = servicio, CotizacionLineaId = linea, PrecioVentaUnitario = venta };

    [Fact]
    public void ElMismoServicioCapturadoEnDosRutasTomaLaVentaDeSuPropiaRuta()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], margen: 0.20m, tipo: TipoSolicitud.Spot);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("SRV", "Cargue", 10_000m, 1m, "SRV")], _ahora);
        cotizacion.CambiarConexos(2, [new clsCambioConexo("SRV", "Cargue", 10_000m, 1m, "SRV")], _ahora);

        var ventas = clsServiciosConexosCosteoService.VentasDeNexo(cotizacion, Nexo(Fila("R1", "SRV", "CL1", 15_000m), Fila("R2", "SRV", "CL2", 20_000m)));
        cotizacion.RefrescarVentaConexos(ventas).Should().Be(2);

        var rutas = cotizacion.RutasOrdenadas;
        rutas[0].Conexos.Single().PrecioVenta.Should().Be(15_000m);
        rutas[1].Conexos.Single().PrecioVenta.Should().Be(20_000m);
    }

    [Fact]
    public void SinLineaDeCotizacionEnNexoNoSeAdivinaLaRuta()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], margen: 0.20m, tipo: TipoSolicitud.Spot);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("SRV", "Cargue", 10_000m, 1m, "SRV")], _ahora);

        var sinLinea = new clsConexoNexo("R1", "Cargue", "logistico", 1m, 10_000m) { ServicioConexoId = "SRV", PrecioVentaUnitario = 15_000m };

        clsServiciosConexosCosteoService.VentasDeNexo(cotizacion, Nexo(sinLinea)).Should().BeEmpty();
    }

    [Fact]
    public void EnLaMismaRutaElCapturadoNoSeBloqueaPorLaFilaQueYaEsDeOtroConexo()
    {
        // Ruta 1: el conexo capturado (id del catálogo SRV, guardado al costo antes del despliegue) y el MISMO servicio agregado después
        // por el POST (fila de Nexo R2, que en Cotifly es el conexo "R2"). R2 ya tiene dueño por id: el capturado debería cruzar solo con R1.
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], margen: 0.20m, tipo: TipoSolicitud.Spot);
        cotizacion.CambiarConexos(1,
        [
            new clsCambioConexo("SRV", "Cargue", 10_000m, 1m, "SRV"),
            new clsCambioConexo("R2", "Cargue", 10_000m, 1m, "SRV", PrecioVenta: 20_000m),
        ], _ahora);

        var ventas = clsServiciosConexosCosteoService.VentasDeNexo(cotizacion, Nexo(Fila("R1", "SRV", "CL1", 15_000m), Fila("R2", "SRV", "CL1", 20_000m)));
        cotizacion.RefrescarVentaConexos(ventas);

        var ruta = cotizacion.RutasOrdenadas[0];
        ruta.Conexos.Single(c => c.IdServicioConexo == "R2").PrecioVenta.Should().Be(20_000m);
        ruta.Conexos.Single(c => c.IdServicioConexo == "SRV").PrecioVenta.Should().Be(15_000m,
            "R2 ya es del otro conexo de la ruta: la única fila libre del servicio es R1");
    }

    [Fact]
    public void UnaVentaConocidaIgualAlCostoPorCasualidadSigueAlCostoNuevoEnUnaLecturaSinVenta()
    {
        // Caracterización de la regla "venta por defecto = costo" de Actualizar: si la venta conocida coincide con el costo, una lectura
        // posterior SIN venta y con otro costo la mueve al costo nuevo (no se distingue de una fila al costo por defecto).
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        var ruta = cotizacion.PrimeraRuta!;
        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta,
            [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 50_000m) { PrecioVentaUnitario = 50_000m }], _ahora);
        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta,
            [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 60_000m)], _ahora);

        ruta.Conexos.Single().PrecioVenta.Should().Be(60_000m);
    }

    [Fact]
    public void UnaVentaConocidaDistintaDelCostoSeConservaAunqueCambieElCostoSinVenta()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        var ruta = cotizacion.PrimeraRuta!;
        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta,
            [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 50_000m) { PrecioVentaUnitario = 70_000m }], _ahora);
        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta,
            [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 60_000m)], _ahora);

        ruta.Conexos.Single().Should().Match<clsServicioConexoRuta>(c => c.Precio == 60_000m && c.PrecioVenta == 70_000m);
        ruta.PrecioFinalLinea.Should().Be(3_000_000m + 70_000m);
    }

    [Fact]
    public void ElRefrescoDentroDeLaLecturaDejaElPrecioRecalculadoYCuadrado()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, estado: EstadoCosteo.Entregada);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, 2m);

        clsLecturaCosteo.AplicarFoto(cotizacion,
            clsCotizacionMuestra.Foto([2_550_000m], conexos: [new clsConexoNexo("CX-1", "Cargue", "logistico", 2m, 170_000m) { PrecioVentaUnitario = 100_000m }],
                estadoCotizacion: "Entregada"),
            _ahora.AddMinutes(5), new clsOpcionesCosteoNexo(), new clsPrecioCosteoService()).Should().BeTrue();

        var ruta = cotizacion.PrimeraRuta!;
        ruta.PrecioFinalLinea.Should().Be(3_200_000m);
        cotizacion.PrecioFinal.Should().Be(3_200_000m);
        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP").Should().BeNull();
        (clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta) + clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Sum(c => c.Precio))
            .Should().Be(ruta.PrecioFinalLinea);
    }

    [Fact]
    public void FinalizadaSinPdfTampocoRefresca()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([2_000_000m], usuarioId: Guid.NewGuid(), tipo: TipoSolicitud.Spot);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        cotizacion.Finalizar(cotizacion.UsuarioId!.Value, _ahora).Valor.Should().BeTrue();
        var precio = cotizacion.PrecioFinal;

        cotizacion.RefrescarVentaConexos([new clsVentaConexo(cotizacion.PrimeraRuta!.Id, "CX-1", 999_999m)]).Should().Be(0);

        cotizacion.PrimeraRuta!.Conexos.Single().PrecioVenta.Should().Be(85_000m);
        cotizacion.PrecioFinal.Should().Be(precio);
    }
}
