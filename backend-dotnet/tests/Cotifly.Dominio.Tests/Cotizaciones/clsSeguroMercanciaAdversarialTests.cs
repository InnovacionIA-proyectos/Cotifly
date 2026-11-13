using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Bordes del seguro de la mercancía: valores límite del redondeo, la TRM, la tasa, el seguro en cero, la vista previa del margen y
/// las transiciones del check (quitar, volver a marcar, editar el valor, copiar a la versión nueva). Complementa
/// <see cref="clsSeguroMercanciaTests"/> sin repetirla.
/// </summary>
public sealed class clsSeguroMercanciaAdversarialTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;
    private const decimal Tasa = 0.003450m;

    private static clsCotizacionSolicitud Costeada(
        decimal? valor = 200_000_000m, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m) =>
        clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, moneda: moneda, trm: trm,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor });

    // El seguro NO se redondea a pesos. Se calcula exacto y se guarda con la escala de la columna
    // costoSeguro DECIMAL(18,2) (centavos), con el criterio de SQL Server al ajustar la escala (la mitad se aleja de cero).
    [Theory]
    [InlineData("1000", "0.0005", "0.5", "0.50")]
    [InlineData("3000", "0.0005", "1.5", "1.50")]
    [InlineData("1", "0.005", "0.005", "0.01")]
    [InlineData("3", "0.005", "0.015", "0.02")]
    [InlineData("1", "0.000001", "0.000001", "0.00")]
    [InlineData("0", "0.003450", "0", "0.00")]
    [InlineData("0.01", "0.999999", "0.00999999", "0.01")]
    [InlineData("123456789.99", "0.003450", "425925.9254655", "425925.93")]
    public void ElSeguroNoSeRedondeaAPesosYSeGuardaEnCentavos(string valor, string tasa, string exacto, string guardado)
    {
        var v = decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);
        var t = decimal.Parse(tasa, System.Globalization.CultureInfo.InvariantCulture);

        clsLineaSolicitud.CalcularSeguro(v, t).Should().Be(decimal.Parse(exacto, System.Globalization.CultureInfo.InvariantCulture), "v × t, sin redondear");
        var seguro = clsLineaSolicitud.SeguroGuardado(v, t);
        seguro.Should().Be(decimal.Parse(guardado, System.Globalization.CultureInfo.InvariantCulture));
        decimal.Round(seguro, 2).Should().Be(seguro, "cabe en la escala de costoSeguro DECIMAL(18,2)");
    }

    [Fact]
    public void ElValorMaximoDeLaMercanciaConLaTasaMaximaNoDesbordaLaColumnaDecimal18_2()
    {
        var seguro = clsLineaSolicitud.SeguroGuardado(clsSolicitudCosteo.MaximoValor, 0.999999m);

        seguro.Should().BeLessThan(10_000_000_000_000_000m, "costoSeguro es DECIMAL(18,2): a lo sumo 16 dígitos enteros");
        seguro.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void LasTasasLimiteDelCheckSeAceptanYLasDeFueraLanzan()
    {
        Costeada().PrimeraRuta!.IncluirSeguro(0.000001m).Valor.Should().BeTrue();
        Costeada().PrimeraRuta!.IncluirSeguro(0.999999m).Valor.Should().BeTrue();

        FluentActions.Invoking(() => Costeada().PrimeraRuta!.IncluirSeguro(-0.003450m)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Costeada().PrimeraRuta!.IncluirSeguro(1.5m)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConValorDeLaMercanciaEnCeroElSeguroQuedaMarcadoEnCeroYNoMueveElPrecio()
    {
        var cotizacion = Costeada(valor: 0m);

        cotizacion.CambiarSeguro(1, true, Tasa, _ahora).Valor.Should().BeTrue("0 es un valor, no la falta de valor");

        cotizacion.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 0m);
        cotizacion.PrecioFinal.Should().Be(1_250_000m);
    }

    [Fact]
    public void EnDolaresConTrmCeroElSeguroNoSeConvierteYElPrecioDeLaRutaFallaEnVezDeOmitirlo()
    {
        var ruta = Costeada(moneda: "USD", trm: 4_000m).PrimeraRuta!;
        ruta.IncluirSeguro(Tasa).Valor.Should().BeTrue();

        ruta.ValorSeguro("USD", 0m, out var valor)!.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        valor.Should().BeNull();
        ruta.PrecioRutaCon(0.20m, "margen", "USD", 0m, out var precio).Should().NotBeNull("sin TRM no hay precio en dólares");
        precio.Should().BeNull("nunca un precio en dólares sin el seguro o con el seguro en pesos");
    }

    [Fact]
    public void EnDolaresElSeguroSeConvierteConLaTrmDeLaSolicitudSinRedondearloAntesAPesos()
    {
        var ruta = Costeada(valor: 200_000_290m, moneda: "USD", trm: 3_999.99m).PrimeraRuta!;
        ruta.IncluirSeguro(Tasa).Valor.Should().BeTrue();

        ruta.CostoSeguro.Should().Be(690_001.00m, "200.000.290 × 0,345 % = 690.001,0005, en la escala de la columna (centavos)");
        ruta.ValorSeguro("USD", 3_999.99m, out var valor).Should().BeNull();

        valor.Should().Be(Math.Round(690_001m / 3_999.99m, 2, MidpointRounding.ToEven)).And.Be(172.50m);
    }

    [Fact]
    public void ElSeguroNoDependeDelMargenEnLaVistaPrevia()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;
        var sinSeguro = Costeada().PrimeraRuta!;

        foreach (var margen in new[] { 0.01m, 0.15m, 0.30m, 0.60m })
        {
            ruta.PrecioRutaCon(margen, "margen", cotizacion.Moneda, cotizacion.TrmAplicada, out var con).Should().BeNull();
            sinSeguro.PrecioRutaCon(margen, "margen", cotizacion.Moneda, cotizacion.TrmAplicada, out var sin).Should().BeNull();

            (con - sin).Should().Be(690_000m, $"con margen {margen} la diferencia es exactamente el seguro, sin margen");
        }
    }

    [Fact]
    public void QuitarYVolverAMarcarTomaLaTasaVigenteNuevaNoLaAnterior()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        cotizacion.CambiarSeguro(1, false, Tasa, _ahora).Valor.Should().BeTrue();

        cotizacion.CambiarSeguro(1, true, 0.004m, _ahora).Valor.Should().BeTrue();

        cotizacion.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.TasaSeguroAplicada == 0.004m && r.CostoSeguro == 800_000m);
        cotizacion.PrecioFinal.Should().Be(1_250_000m + 800_000m);
    }

    [Fact]
    public void QuitarElSeguroDeUnaRutaQueQuedoSinValorLoDesmarca()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.AplicarEdicion(Edicion(ruta, null, Tasa), _ahora);
        ruta.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.CostoSeguro == null);

        ruta.QuitarSeguro().Should().BeTrue();

        ruta.Should().Match<clsLineaSolicitud>(r => !r.IncluyeSeguro && r.TasaSeguroAplicada == null && r.CostoSeguro == null);
    }

    [Fact]
    public void QuitarElValorYDevolverloRecalculaConLaTasaVigente()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, null, Tasa), _ahora);
        cotizacion.AplicarEdicion(Edicion(ruta, 100_000_000m, 0.004m), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == 0.004m && r.CostoSeguro == 400_000m);
    }

    [Fact]
    public void UnaEdicionSinTasaVigenteConservaLaTasaDeLaRutaAlRecalcular()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, 100_000_000m, null), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 345_000m);
    }

    [Fact]
    public void UnaEdicionDeUnaRutaSinSeguroNoLaMarca()
    {
        var cotizacion = Costeada();
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, 300_000_000m, Tasa), _ahora);

        ruta.Should().Match<clsLineaSolicitud>(r => !r.IncluyeSeguro && r.TasaSeguroAplicada == null && r.CostoSeguro == null);
    }

    [Fact]
    public void UnaEdicionQuePasaADolaresConvierteElSeguroConLaTrmNueva()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(ruta, 200_000_000m, Tasa) with { MonedaNueva = "USD", TrmNueva = 4_000m }, _ahora);

        ruta.CostoSeguro.Should().Be(690_000m, "en pesos, sin cambio de valor");
        ruta.ValorSeguro(cotizacion.Moneda, cotizacion.TrmAplicada, out var valor).Should().BeNull();
        valor.Should().Be(172.50m);
    }

    [Fact]
    public void LaVersionNuevaNoMarcaElSeguroSiLaAnteriorNoLoTeniaNiEnRutasNuevas()
    {
        var original = Costeada();
        var nueva = Costeada();

        nueva.CopiarSegurosDe(original, Tasa);

        nueva.PrimeraRuta!.IncluyeSeguro.Should().BeFalse();
        nueva.CopiarSegurosDe(original, new Dictionary<Guid, Guid> { [Guid.NewGuid()] = original.PrimeraRuta!.Id }, Tasa);
        nueva.PrimeraRuta!.IncluyeSeguro.Should().BeFalse("la ruta de la versión editada no edita ninguna ruta de la original");
    }

    [Fact]
    public void LaVersionNuevaDeUnaRutaConSeguroSinValorTomaLaTasaVigenteSiAhoraTraeValor()
    {
        var original = Costeada();
        original.CambiarSeguro(1, true, Tasa, _ahora);
        original.AplicarEdicion(Edicion(original.PrimeraRuta!, null, Tasa), _ahora);
        var nueva = Costeada(valor: 100_000_000m);

        nueva.CopiarSegurosDe(original, 0.004m);

        nueva.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == 0.004m && r.CostoSeguro == 400_000m);
    }

    [Fact]
    public void LaVersionNuevaCopiaElSeguroDeLaRutaCorrectaPorId()
    {
        var original = Costeada();
        original.CambiarSeguro(1, true, Tasa, _ahora);
        var nueva = Costeada(valor: 200_000_000m);

        nueva.CopiarSegurosDe(original, new Dictionary<Guid, Guid> { [nueva.PrimeraRuta!.Id] = original.PrimeraRuta!.Id }, 0.009m);

        nueva.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.TasaSeguroAplicada == Tasa && r.CostoSeguro == 690_000m,
            "mismo valor: la tasa vigente nueva no se toma");
    }

    [Fact]
    public void UnErrorDeValidacionNoDejaElSeguroAMedias()
    {
        var finalizada = Costeada();
        finalizada.Finalizar(Guid.NewGuid(), _ahora).Valor.Should().BeTrue();
        var precio = finalizada.PrecioFinal;

        finalizada.CambiarSeguro(1, true, Tasa, _ahora).EsFallo.Should().BeTrue();
        finalizada.CambiarSeguro(1, false, Tasa, _ahora).EsFallo.Should().BeTrue("tampoco se puede quitar en una finalizada");

        finalizada.PrimeraRuta!.Should().Match<clsLineaSolicitud>(r => !r.IncluyeSeguro && r.TasaSeguroAplicada == null && r.CostoSeguro == null);
        finalizada.PrecioFinal.Should().Be(precio);
    }

    [Fact]
    public void UnaRutaInexistenteNoEsUnCambioSilencioso()
    {
        var cotizacion = Costeada();

        FluentActions.Invoking(() => cotizacion.CambiarSeguro(9, true, Tasa, _ahora)).Should().Throw<ArgumentOutOfRangeException>(
            "el caso de uso resuelve la ruta antes (RUTA_INEXISTENTE); el dominio no inventa una");
        cotizacion.PrimeraRuta!.IncluyeSeguro.Should().BeFalse();
    }

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
