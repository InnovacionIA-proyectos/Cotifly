using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Cotización en dólares de punta a punta en el agente (HU-012.2, ADR-013): la moneda sale del selector del formulario o del mensaje del
/// comercial (nunca de la iniciativa del modelo), la TRM la consulta el servidor y queda en el estado y en la trazabilidad del PDF; si la
/// TRM falla no hay precio en USD.
/// </summary>
public sealed class clsMonedaUsdTests
{
    private static readonly object _payload = new
    {
        cliente = "Longyin",
        linea_negocio = "Nacional",
        tarifa = new { titulo = "COTIZACIÓN TRANSPORTE NACIONAL", origen = "YUMBO", filas = new[] { new { destino = "BUENAVENTURA", vehiculo_peso = "Tractomula - 34 TON" } } },
    };

    private readonly clsArnesAgente _arnes = new();

    [Fact]
    public async Task ConElSelectorEnUsdElPrecioSeConvierteConLaTrmYLlegaAlPdf()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("ok")
            .Llamar("generar_pdf", new { cotizacion = _payload })
            .Cerrar("Listo.");

        var turno1 = (await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD")).Valor;
        await _arnes.TurnoAsync("Confirmo", moneda: "USD");

        var precio = turno1.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;
        precio.Moneda.Should().Be("USD");
        precio.Precio.Should().Be(996.35m);
        precio.PrecioCop.Should().Be(3_294_798m);
        precio.TrmAplicada.Should().Be(clsArnesAgente.TrmDelDia);
        precio.Costo.Should().Be(2_850_000m);
        await _arnes.Trm.Received().ObtenerAsync(new DateOnly(2026, 9, 24), Arg.Any<CancellationToken>());

        var pdf = _arnes.EntradasPdf.Should().ContainSingle().Subject;
        pdf.Trazabilidad!.Moneda.Should().Be("USD");
        pdf.Trazabilidad.TrmAplicada.Should().Be(clsArnesAgente.TrmDelDia);
        pdf.Trazabilidad.PrecioFinal.Should().Be(996.35m);
        pdf.Trazabilidad.Costo.Should().Be(2_850_000m);
        var fila = pdf.Cotizacion.Tarifa!.Filas!.Single();
        fila.Tarifa.Should().Be(996.35m);
        fila.Moneda.Should().Be("USD");
    }

    [Fact]
    public async Task EnPesosLaTrmDeLaTrazabilidadEsCeroYNoSeConsulta()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("ok")
            .Llamar("generar_pdf", new { cotizacion = _payload })
            .Cerrar("Listo.");

        await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "COP");
        await _arnes.TurnoAsync("Confirmo");

        var traza = _arnes.EntradasPdf.Single().Trazabilidad!;
        traza.Moneda.Should().Be("COP");
        traza.TrmAplicada.Should().Be(0m);
        traza.PrecioFinal.Should().Be(3_294_798m);
        await _arnes.Trm.DidNotReceive().ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElModeloNoConvierteADolaresSiElComercialNoLoPidio()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen", moneda = "USD" })
            .Cerrar("ok");

        var precio = (await _arnes.TurnoAsync("cotiza con costo 2.850.000")).Valor.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;

        precio.Moneda.Should().Be("COP");
        precio.Precio.Should().Be(3_294_798m);
        precio.TrmAplicada.Should().Be(0m);
    }

    [Theory]
    [InlineData("cotiza en dólares con costo 2.850.000")]
    [InlineData("cotiza con costo 2.850.000, en USD por favor")]
    [InlineData("la quiero en dolares, costo 2.850.000")]
    public async Task SinSelectorElComercialPideDolaresEnSuMensaje(string mensaje)
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen", moneda = "USD" })
            .Cerrar("ok");

        var precio = (await _arnes.TurnoAsync(mensaje)).Valor.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;

        precio.Moneda.Should().Be("USD");
        precio.Precio.Should().Be(996.35m);
        _arnes.Estado().Moneda.Should().Be("USD");
        _arnes.Estado().TrmAplicada.Should().Be(clsArnesAgente.TrmDelDia);
    }

    [Fact]
    public async Task ElSelectorMandaSobreLaMonedaQuePideElModelo()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen", moneda = "COP" })
            .Cerrar("ok");

        var precio = (await _arnes.TurnoAsync("cotiza en pesos con costo 2.850.000", moneda: "USD")).Valor.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;

        precio.Moneda.Should().Be("USD");
    }

    [Fact]
    public async Task SiLaTrmFallaNoHayPrecioEnDolaresNiPdf()
    {
        _arnes.Trm.ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "La TRM no está disponible."));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("La TRM del día no está disponible: puedes reintentar o cotizar en pesos.")
            .Llamar("generar_pdf", new { cotizacion = _payload })
            .Cerrar("No se pudo.");

        var turno1 = (await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD")).Valor;
        var turno2 = (await _arnes.TurnoAsync("Confirmo", moneda: "USD")).Valor;

        turno1.Salidas.Last().Error!.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        turno1.UltimaSalida<clsSalidaPrecio>("calcular_precio").Should().BeNull();
        _arnes.Estado().PrecioFinal.Should().BeNull();
        turno2.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido, "sin precio calculado no hay PDF");
        _arnes.EntradasPdf.Should().BeEmpty();
    }

    [Fact]
    public async Task CambiarLaMonedaEnElSelectorDescartaElPrecioCalculado()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("ok")
            .Cerrar("Recalculo en dólares.");

        await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "COP");
        _arnes.Estado().PrecioFinal.Should().Be(3_294_798m);

        await _arnes.TurnoAsync("ahora en dólares", moneda: "USD");

        var estado = _arnes.Estado();
        estado.Moneda.Should().Be("USD");
        estado.PrecioFinal.Should().BeNull();
        estado.TrmAplicada.Should().BeNull();
        estado.ConfirmacionPendiente.Should().BeFalse();
    }

    [Fact]
    public async Task ElAjusteNegociadoEnDolaresConvierteElPrecio()
    {
        _arnes.Ajuste.RegistrarAsync(Arg.Any<clsEntradaAjusteMargen>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaAjusteMargen>.Exito(new clsSalidaAjusteMargen(
                new clsSalidaMargen(0.11m, "negociado", null, false, "Ajuste"), true, -0.025m, Guid.NewGuid(), new clsSalidaPrecio(3_202_247m, 2_850_000m, 0.11m, "margen", 352_247m))));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Llamar("registrar_ajuste_margen", new { margenAplicado = 0.11, motivo = "Negociación" })
            .Cerrar("ok");

        await _arnes.TurnoAsync("Costo 2.850.000; ajusta el margen a 11%", moneda: "USD");

        var estado = _arnes.Estado();
        estado.PrecioFinal.Should().Be(968.36m);
        estado.Moneda.Should().Be("USD");
        estado.TrmAplicada.Should().Be(clsArnesAgente.TrmDelDia);
    }

    [Fact]
    public async Task SiLaTrmFallaElAjusteEnDolaresNoSeRegistra()
    {
        _arnes.Trm.ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "La TRM no está disponible."));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("registrar_ajuste_margen", new { margenAplicado = 0.11, motivo = "Negociación" })
            .Cerrar("ok");

        var turno = (await _arnes.TurnoAsync("Costo 2.850.000; ajusta el margen a 11%", moneda: "USD")).Valor;

        turno.Salidas.Last().Error!.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        await _arnes.Ajuste.DidNotReceive().RegistrarAsync(Arg.Any<clsEntradaAjusteMargen>(), Arg.Any<CancellationToken>());
    }
}
