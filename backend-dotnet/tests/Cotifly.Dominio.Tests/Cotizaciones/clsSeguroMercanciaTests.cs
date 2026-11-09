using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-045.2 (#264, ADR-029): el seguro de la mercancía por ruta. <c>costoSeguro = round(valorMercancia × tasa, 0)</c> en pesos, sin margen,
/// sumado al precio de la ruta después del margen y de la venta de los conexos (decisión 1); en USD, convertido con la TRM de la solicitud
/// (decisión 2). La tasa se copia en la ruta al marcarlo y otra vez si cambia el valor de la mercancía (decisión 5).
/// </summary>
public sealed class clsSeguroMercanciaTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;
    private static readonly Guid _ana = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Tasa vigente de la HU: 0,345 %.</summary>
    private const decimal Tasa = 0.003450m;

    /// <summary>Formulario costeado: flete 1.000.000 con margen 20 % (precio 1.250.000) y la mercancía en <paramref name="valor"/>.</summary>
    private static clsCotizacionSolicitud Costeada(
        decimal? valor = 200_000_000m, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m) =>
        clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, moneda: moneda, trm: trm, usuarioId: _ana,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor });

    [Fact]
    public void IncluirElSeguroSubeElPrecioDeLaRutaYElTotalExactamenteElSeguroSinMargen()
    {
        var cotizacion = Costeada();
        var ruta = cotizacion.PrimeraRuta!;
        ruta.PrecioFinalLinea.Should().Be(1_250_000m);

        var cambio = cotizacion.CambiarSeguro(1, incluir: true, Tasa, _ahora);

        cambio.Valor.Should().BeTrue();
        ruta.IncluyeSeguro.Should().BeTrue();
        ruta.TasaSeguroAplicada.Should().Be(Tasa);
        ruta.CostoSeguro.Should().Be(690_000m, "200.000.000 × 0,345 % (escenario de la HU)");
        ruta.PrecioFinalLinea.Should().Be(1_940_000m, "1.250.000 + 690.000: el seguro no lleva margen");
        cotizacion.PrecioFinal.Should().Be(1_940_000m);
        ruta.CostoFinal.Should().Be(1_000_000m, "el seguro no entra en el costo final (costo de Nexo)");
    }

    [Fact]
    public void QuitarElSeguroDejaLaTasaYElCostoVaciosYElPrecioSinSeguro()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);

        var cambio = cotizacion.CambiarSeguro(1, incluir: false, Tasa, _ahora);

        cambio.Valor.Should().BeTrue();
        var ruta = cotizacion.PrimeraRuta!;
        ruta.Should().Match<clsLineaSolicitud>(r => !r.IncluyeSeguro && r.TasaSeguroAplicada == null && r.CostoSeguro == null);
        ruta.PrecioFinalLinea.Should().Be(1_250_000m);
        cotizacion.PrecioFinal.Should().Be(1_250_000m);
    }

    [Fact]
    public void MarcarDosVecesOQuitarSinSeguroNoCambiaNada()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, false, Tasa, _ahora).Valor.Should().BeFalse("no tenía seguro");
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora).Valor.Should().BeTrue();

        cotizacion.CambiarSeguro(1, true, 0.004m, _ahora).Valor.Should().BeFalse("ya lo tiene: conserva su tasa aunque la vigente cambie");

        cotizacion.PrimeraRuta!.TasaSeguroAplicada.Should().Be(Tasa);
        cotizacion.PrimeraRuta!.CostoSeguro.Should().Be(690_000m);
    }

    [Fact]
    public void UnCambioDeLaTasaNoTocaLaRutaQueYaTieneElSeguroYLaNuevaUsaLaVigente()
    {
        var antes = Costeada();
        antes.CambiarSeguro(1, true, Tasa, _ahora);
        var despues = Costeada();

        despues.CambiarSeguro(1, true, 0.004m, _ahora);

        antes.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 690_000m);
        despues.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.TasaSeguroAplicada == 0.004m && r.CostoSeguro == 800_000m);
    }

    [Fact]
    public void SinValorDeLaMercanciaRespondeSeguroSinValorMercanciaYNadaCambia()
    {
        var cotizacion = Costeada(valor: null);

        var cambio = cotizacion.CambiarSeguro(1, true, Tasa, _ahora);

        cambio.EsFallo.Should().BeTrue();
        cambio.Error.Codigo.Should().Be(clsCodigosError.SeguroSinValorMercancia);
        cambio.Error.Datos.Should().ContainKey("linea_orden").WhoseValue.Should().Be(1);
        cotizacion.PrimeraRuta!.IncluyeSeguro.Should().BeFalse();
        cotizacion.PrecioFinal.Should().Be(1_250_000m);
    }

    [Fact]
    public void FinalizadaOEntregadaRespondeElMismoPdfYaEnviadoQueElMargenYNadaCambia()
    {
        var finalizada = Costeada();
        finalizada.Finalizar(_ana, _ahora).Valor.Should().BeTrue();
        var entregada = Costeada();
        entregada.RegistrarEnvio();

        foreach (var cotizacion in new[] { finalizada, entregada })
        {
            var cambio = cotizacion.CambiarSeguro(1, true, Tasa, _ahora);

            cambio.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
            cotizacion.ValidarAjusteMargen(1, 0.25m, null)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado, "el mismo 409 que el margen");
            cotizacion.PrimeraRuta!.IncluyeSeguro.Should().BeFalse();
        }
    }

    [Fact]
    public void EnDolaresElSeguroSeCalculaEnPesosYSeConvierteConLaTrmDeLaSolicitud()
    {
        var cotizacion = Costeada(moneda: "USD", trm: 4_000m);
        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(312.50m, "1.250.000 / 4.000");

        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);

        var ruta = cotizacion.PrimeraRuta!;
        ruta.CostoSeguro.Should().Be(690_000m, "el seguro se guarda en pesos");
        ruta.ValorSeguro(cotizacion.Moneda, cotizacion.TrmAplicada, out var enDolares).Should().BeNull();
        enDolares.Should().Be(172.50m, "690.000 / 4.000");
        ruta.PrecioFinalLinea.Should().Be(485.00m, "312,50 + 172,50");
        cotizacion.PrecioFinal.Should().Be(485.00m);
    }

    [Fact]
    public void ElSeguroNoSeRedondeaAPesos()
    {
        // Decisión del usuario del 2026-11-09: sin redondeo por ahora; se guarda con la escala de la columna (DECIMAL(18,2)).
        clsLineaSolicitud.CalcularSeguro(123_456_789m, Tasa).Should().Be(425_925.92205m, "123.456.789 × 0,345 %, exacto");
        clsLineaSolicitud.SeguroGuardado(123_456_789m, Tasa).Should().Be(425_925.92m, "en centavos, la escala de costoSeguro");
        clsLineaSolicitud.CalcularSeguro(300_000_000m, Tasa).Should().Be(1_035_000m, "escenario de la HU");
    }

    [Fact]
    public void UnSeguroConCentavosSeSumaConSusCentavosAlPrecio()
    {
        var cotizacion = Costeada(valor: 123_456_789m);

        cotizacion.CambiarSeguro(1, true, Tasa, _ahora).Valor.Should().BeTrue();

        cotizacion.PrimeraRuta!.CostoSeguro.Should().Be(425_925.92m);
        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(1_250_000m + 425_925.92m, "sin redondear el seguro a pesos");
    }

    [Fact]
    public void LaVistaPreviaDelMargenIncluyeElSeguroSinMargen()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);

        cotizacion.PrimeraRuta!.PrecioRutaCon(0.50m, "margen", cotizacion.Moneda, cotizacion.TrmAplicada, out var precio).Should().BeNull();

        precio.Should().Be(2_690_000m, "1.000.000 / 0,5 = 2.000.000 + 690.000");
    }

    [Fact]
    public void ElSeguroSeSumaDespuesDeLaVentaDeLosConexos()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 50_000m, 1m, PrecioVenta: 70_000m)], _ahora);

        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);

        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(1_250_000m + 70_000m + 690_000m);
    }

    [Fact]
    public void SinFleteLaRutaConSeguroNoTienePrecio()
    {
        var cotizacion = clsCotizacionMuestra.Formulario(solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 200_000_000m });

        cotizacion.CambiarSeguro(1, true, Tasa, _ahora).Valor.Should().BeTrue("el seguro se puede marcar antes del costeo");

        cotizacion.PrimeraRuta!.CostoSeguro.Should().Be(690_000m);
        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().BeNull("sin flete no hay precio, como con los conexos");
    }

    [Fact]
    public void LaLicitacionNoAdmiteElSeguroTodavia()
    {
        var licitacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m]);

        var cambio = licitacion.CambiarSeguro(1, true, Tasa, _ahora);

        cambio.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        cambio.Error.Detalle.Should().Be(clsCotizacionSolicitud.MotivoSeguroLicitacion);
    }

    [Fact]
    public void LaRutaRechazadaPorLaMesaNoAdmiteElSeguro()
    {
        var cotizacion = Costeada();
        cotizacion.PrimeraRuta!.Vehiculos[0].RegistrarRechazo("Ruta no operada", _ahora);
        cotizacion.PrimeraRuta!.ResumirEstado(true);

        cotizacion.CambiarSeguro(1, true, Tasa, _ahora).Error.Codigo.Should().Be(clsCodigosError.RutaRechazada);
    }

    [Fact]
    public void AlEditarElValorDeLaMercanciaElSeguroSeRecalculaConLaTasaVigente()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, 300_000_000m, tasaVigente: Tasa), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 1_035_000m,
            "escenario de la HU: 300.000.000 × 0,345 %");
    }

    [Fact]
    public void UnaEdicionQueNoCambiaElValorNoVuelveATomarLaTasa()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, 200_000_000m, tasaVigente: 0.004m), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 690_000m);
    }

    [Fact]
    public void ConOtroValorYUnaTasaVigenteNuevaElSeguroTomaLaNueva()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, 300_000_000m, tasaVigente: 0.004m), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => r.TasaSeguroAplicada == 0.004m && r.CostoSeguro == 1_200_000m, "decisión 5");
    }

    [Fact]
    public void SinValorDeLaMercanciaElSeguroSigueMarcadoSinTasaNiCosto()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, null, tasaVigente: Tasa), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == null && r.CostoSeguro == null);
        ruta.ValorSeguro(clsCotizacionSolicitud.MonedaPesos, 0m, out var valor).Should().BeNull();
        valor.Should().BeNull();
    }

    [Fact]
    public void LaVersionNuevaCopiaElSeguroYLoRecalculaSiCambiaElValor()
    {
        var original = Costeada();
        original.CambiarSeguro(1, true, Tasa, _ahora);
        var igual = Costeada();
        var otroValor = Costeada(valor: 300_000_000m);

        igual.CopiarSegurosDe(original, 0.004m);
        otroValor.CopiarSegurosDe(original, 0.004m);

        igual.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 690_000m,
            "mismo valor: conserva la tasa y el costo de la versión anterior");
        otroValor.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == 0.004m && r.CostoSeguro == 1_200_000m,
            "otro valor: toma la tasa vigente (decisión 5)");
    }

    [Fact]
    public void UnaTasaFueraDeRangoEsUnErrorDeProgramacion()
    {
        var ruta = Costeada().PrimeraRuta!;

        FluentActions.Invoking(() => ruta.IncluirSeguro(0m)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ruta.IncluirSeguro(1m)).Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Edición en sitio de la ruta <paramref name="ruta"/> (HU-028) con otro valor de la mercancía y la tasa vigente leída.</summary>
    private static clsEdicionSolicitud Edicion(clsLineaSolicitud ruta, decimal? valor, decimal? tasaVigente)
    {
        var linea = clsSolicitudCosteoMuestra.RutaCaptura() with { ValorMercancia = valor };
        var capturada = new clsRutaCapturada(linea, 3, 0, [new clsCamionPlaneado(null, null, linea.PesoKg)], []);
        return new clsEdicionSolicitud(
            new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline },
            [new clsRutaEditada(ruta.Id, capturada, Replanificada: false)])
        {
            TasaSeguroVigente = tasaVigente,
        };
    }
}
