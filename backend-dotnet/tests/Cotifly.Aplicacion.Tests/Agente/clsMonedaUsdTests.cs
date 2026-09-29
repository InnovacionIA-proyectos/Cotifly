using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Cotización en dólares de punta a punta en el agente (HU-012.2, ADR-014): la moneda sale del selector del formulario o del mensaje del
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

    // Auditoría de HU-012 (c): Google Chat, sin selector. "En dólares" del turno 1 vale cuando se calcula el precio en el turno 3.
    [Fact]
    public async Task LaMonedaNombradaEnUnTurnoAnteriorValeAlCalcularElPrecio()
    {
        _arnes.Modelo
            .Cerrar("¿Cuál es el costo?")
            .Cerrar("¿Algo más de la ruta?")
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("ok");

        await _arnes.TurnoAsync("Cotiza Nacional Yumbo a Buenaventura en dólares");
        await _arnes.TurnoAsync("el costo es 2.850.000 pesos");
        var precio = (await _arnes.TurnoAsync("calcula el precio con costo 2.850.000")).Valor.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;

        precio.Moneda.Should().Be("USD");
        precio.Precio.Should().Be(996.35m);
    }

    // Auditoría (e): si al confirmar la TRM ya no rige, el servidor recalcula con la vigente y vuelve a previsualizar, sin emitir el PDF.
    [Fact]
    public async Task ConfirmarConLaTrmVencidaRecalculaYVuelveAPrevisualizar()
    {
        var manana = new DateOnly(2026, 9, 25);
        _arnes.Trm.ObtenerAsync(manana, Arg.Any<CancellationToken>()).Returns(new clsTrm(3400m, manana, manana, "prueba"));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("Previsualización en USD.")
            .Llamar("generar_pdf", new { cotizacion = _payload })
            .Cerrar("La TRM cambió: nueva previsualización.")
            .Llamar("generar_pdf", new { cotizacion = _payload })
            .Cerrar("Listo.");

        await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD");
        _arnes.Calendario.Hoy.Returns(manana);
        var confirmacionVencida = (await _arnes.TurnoAsync("Confirmo", moneda: "USD")).Valor;

        confirmacionVencida.Salidas.Select(x => x.Herramienta).Should().Equal("calcular_precio", "generar_pdf");
        confirmacionVencida.Salidas[0].EsExitoso.Should().BeTrue("el recálculo del servidor es una salida del turno");
        confirmacionVencida.Salidas[1].Error!.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
        _arnes.EntradasPdf.Should().BeEmpty("con la TRM vencida no se emite el PDF");
        var estado = _arnes.Estado();
        estado.TrmAplicada.Should().Be(3400m);
        estado.PrecioFinal.Should().Be(969.06m); // 3.294.798 / 3.400
        estado.PrecioCop.Should().Be(3_294_798m);
        estado.TrmVigenciaHasta.Should().Be(manana);
        estado.ConfirmacionPendiente.Should().BeTrue("hay una nueva previsualización por confirmar");
        _arnes.Modelo.Llamadas.Should().Contain(l => l.Mensajes.Any(m => m.Text.Contains("recalculó el precio", StringComparison.Ordinal)));

        await _arnes.TurnoAsync("Confirmo", moneda: "USD");

        var pdf = _arnes.EntradasPdf.Should().ContainSingle().Subject;
        pdf.Trazabilidad!.TrmAplicada.Should().Be(3400m);
        pdf.Trazabilidad.PrecioFinal.Should().Be(969.06m);
    }

    [Fact]
    public async Task ConfirmarConLaTrmVencidaYSinTrmVigenteDescartaElPrecio()
    {
        var manana = new DateOnly(2026, 9, 25);
        _arnes.Trm.ObtenerAsync(manana, Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "caída"));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("Previsualización en USD.")
            .Cerrar("La TRM de hoy no está disponible.");

        await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD");
        _arnes.Calendario.Hoy.Returns(manana);
        await _arnes.TurnoAsync("Confirmo", moneda: "USD");

        _arnes.Estado().PrecioFinal.Should().BeNull();
        _arnes.Estado().ConfirmacionPendiente.Should().BeFalse();
        _arnes.EntradasPdf.Should().BeEmpty();
    }

    // Auditoría (g): si el precio negociado no se puede convertir, TRM_NO_DISPONIBLE; nunca un precio en pesos en silencio.
    [Fact]
    public async Task ElAjusteEnDolaresSinTrmValidaEsTrmNoDisponibleYNoQuedaEnPesos()
    {
        _arnes.Trm.ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsTrm(0m, ci.Arg<DateOnly>(), ci.Arg<DateOnly>(), "prueba"));
        _arnes.Ajuste.RegistrarAsync(Arg.Any<clsEntradaAjusteMargen>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaAjusteMargen>.Exito(new clsSalidaAjusteMargen(
                new clsSalidaMargen(0.11m, "negociado", null, false, "Ajuste"), true, -0.025m, Guid.NewGuid(), new clsSalidaPrecio(3_202_247m, 2_850_000m, 0.11m, "margen", 352_247m))));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("registrar_ajuste_margen", new { margenAplicado = 0.11, motivo = "Negociación" })
            .Cerrar("ok");

        var turno = (await _arnes.TurnoAsync("Costo 2.850.000; ajusta el margen a 11%", moneda: "USD")).Valor;

        turno.Salidas.Last().Error!.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        _arnes.Estado().PrecioFinal.Should().BeNull("no queda un precio en pesos marcado como USD");
    }

    // Auditoría (b): el costo siempre es en pesos; una tarifa en otra moneda se rechaza.
    [Fact]
    public async Task UnaTarifaQueNoEstaEnPesosSeRechaza()
    {
        _arnes.Tarifas.BuscarAsync(Arg.Any<clsEntradaBuscarTarifa>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaTarifa>.Exito(new clsSalidaTarifa(true, 800m, "USD", "nexo", "C-9")));
        _arnes.Modelo
            .Llamar("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "YUMBO", destino = "BUN", pesoKg = 10000 })
            .Cerrar("La tarifa no está en pesos.");

        var turno = (await _arnes.TurnoAsync("cotiza")).Valor;

        var error = turno.Salidas.Single().Error!;
        error.Codigo.Should().Be(clsCodigosError.TarifaConfigInvalida);
        error.Mensaje.Should().Contain("USD").And.Contain("COP");
        _arnes.Estado().Costo.Should().BeNull();
    }

    // Auditoría (a): la respuesta del chat trae los errores de negocio de las herramientas para el frontend.
    [Fact]
    public async Task LaRespuestaDelChatTraeLosErroresDeLasHerramientas()
    {
        _arnes.Trm.ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "caída"));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("La TRM no está disponible.");

        var respuesta = clsRespuestaChat.Desde((await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD")).Valor);

        respuesta.Errores.Should().Equal(new clsErrorHerramienta("calcular_precio", clsCodigosError.TrmNoDisponible));
        respuesta.Precio.Should().BeNull();
    }

    // Validación de HU-012 (6): el recálculo por TRM vencida es una salida de calcular_precio del turno (RespuestaChat.precio).
    [Fact]
    public async Task ElRecalculoPorTrmVencidaLlegaALaRespuestaDelChat()
    {
        var manana = new DateOnly(2026, 9, 25);
        _arnes.Trm.ObtenerAsync(manana, Arg.Any<CancellationToken>()).Returns(new clsTrm(3400m, manana, manana, "prueba"));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("Previsualización en USD.")
            .Cerrar("La TRM cambió: nueva previsualización.");

        await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD");
        _arnes.Calendario.Hoy.Returns(manana);
        var turno = (await _arnes.TurnoAsync("Confirmo", moneda: "USD")).Valor;

        var respuesta = clsRespuestaChat.Desde(turno);
        respuesta.Precio.Should().NotBeNull();
        respuesta.Precio!.Precio.Should().Be(969.06m);
        respuesta.Precio.TrmAplicada.Should().Be(3400m);
        respuesta.Errores.Should().BeEmpty();
    }

    [Fact]
    public async Task ElRecalculoSinTrmVigenteLlegaComoErrorDeLaRespuesta()
    {
        var manana = new DateOnly(2026, 9, 25);
        _arnes.Trm.ObtenerAsync(manana, Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "caída"));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("Previsualización en USD.")
            .Cerrar("La TRM de hoy no está disponible.");

        await _arnes.TurnoAsync("cotiza con costo 2.850.000", moneda: "USD");
        _arnes.Calendario.Hoy.Returns(manana);
        var respuesta = clsRespuestaChat.Desde((await _arnes.TurnoAsync("Confirmo", moneda: "USD")).Valor);

        respuesta.Precio.Should().BeNull();
        respuesta.Errores.Should().Equal(new clsErrorHerramienta("calcular_precio", clsCodigosError.TrmNoDisponible));
    }
}
