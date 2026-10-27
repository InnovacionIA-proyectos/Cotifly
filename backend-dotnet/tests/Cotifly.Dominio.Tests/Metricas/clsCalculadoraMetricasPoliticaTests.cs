using Cotifly.Dominio.Metricas;

namespace Cotifly.Dominio.Tests.Metricas;

/// <summary>
/// HU-016.1 (#154, parte B; decisiones P3 y P4 del 2026-10-27): el margen aplicado se compara con la política de las MISMAS rutas
/// (<see cref="clsRutaMetrica.MargenPolitica"/>), en promedio simple y ponderado por costo; el semáforo
/// (<see cref="clsIndicadoresMetricas.DesviacionMargenPolitica"/>) usa el ponderado.
/// </summary>
public sealed class clsCalculadoraMetricasPoliticaTests
{
    private static readonly DateTimeOffset _marzo = new(2026, 3, 10, 12, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset _ahora = new(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(-5));

    private static clsRutaMetrica Ruta(int orden, decimal? costo, decimal? margen, decimal? politica, decimal? precio = null) => new()
    {
        Orden = orden,
        LineaNegocioId = 1,
        LineaNegocio = "Nacional",
        Costo = costo,
        PrecioFinal = precio ?? (costo is { } c && margen is { } m && m < 1m ? Math.Round(c / (1m - m), 0) : null),
        MargenAplicado = margen,
        MargenPolitica = politica,
    };

    private static clsCotizacionMetrica Cotizacion(params clsRutaMetrica[] rutas) => Cotizacion("COP", 0m, rutas);

    private static clsCotizacionMetrica Cotizacion(string moneda, decimal trm, params clsRutaMetrica[] rutas) => new()
    {
        UsuarioId = Guid.NewGuid(),
        CreatedAt = _marzo,
        CreatedAtVigente = _marzo,
        Moneda = moneda,
        TrmAplicada = trm,
        Costo = rutas.All(r => r.Costo.HasValue) ? rutas.Sum(r => r.Costo!.Value) : null,
        PrecioFinal = rutas.All(r => r.PrecioFinal.HasValue) ? rutas.Sum(r => r.PrecioFinal!.Value) : null,
        Rutas = rutas,
    };

    private static clsIndicadoresMetricas Indicadores(params clsCotizacionMetrica[] filas) =>
        clsCalculadoraMetricas.Indicadores(filas, _ahora, deadlineDisponible: false);

    /// <summary>Criterio de aceptación del plan: 3 rutas con margen 10/20/30 % y política 15/15/25 %, costos 1M, 2M y 3M.</summary>
    [Fact]
    public void TresRutasDanElPromedioSimpleYElPonderadoPorCostoExactos()
    {
        var indicadores = Indicadores(Cotizacion(
            Ruta(1, 1_000_000m, 0.10m, 0.15m),
            Ruta(2, 2_000_000m, 0.20m, 0.15m),
            Ruta(3, 3_000_000m, 0.30m, 0.25m)));

        indicadores.MargenPoliticaPromedio.Should().Be(0.1833m, "(0,15 + 0,15 + 0,25) / 3 = 0,18333…");
        indicadores.MargenPoliticaPonderado.Should().Be(0.2m, "(0,15 × 1M + 0,15 × 2M + 0,25 × 3M) / 6M = 1,2M / 6M");
        indicadores.DesviacionMargenPolitica.Should().Be(0.0333m, "(−0,05 × 1M + 0,05 × 2M + 0,05 × 3M) / 6M = 0,2M / 6M");
        indicadores.RutasSinMargenPolitica.Should().Be(0);
    }

    [Fact]
    public void LaDesviacionEsElAplicadoPonderadoMenosLaPoliticaPonderadaSobreLasMismasRutas()
    {
        // Aplicado ponderado = (0,10 × 4M + 0,12 × 1M) / 5M = 0,104; política ponderada = (0,15 × 4M + 0,15 × 1M) / 5M = 0,15.
        var indicadores = Indicadores(
            Cotizacion(Ruta(1, 4_000_000m, 0.10m, 0.15m)),
            Cotizacion(Ruta(1, 1_000_000m, 0.12m, 0.15m)));

        indicadores.MargenPoliticaPonderado.Should().Be(0.15m);
        indicadores.DesviacionMargenPolitica.Should().Be(-0.046m, "0,104 − 0,15: por debajo de la política, semáforo rojo");
        indicadores.MargenPoliticaPromedio.Should().Be(0.15m);
    }

    [Fact]
    public void UnaRutaSinPoliticaNoEntraYSeCuenta()
    {
        var indicadores = Indicadores(
            Cotizacion(Ruta(1, 1_000_000m, 0.20m, 0.15m)),
            Cotizacion(Ruta(1, 9_000_000m, 0.05m, null)));

        indicadores.MargenPoliticaPromedio.Should().Be(0.15m);
        indicadores.MargenPoliticaPonderado.Should().Be(0.15m, "la ruta de 9M sin política no pesa");
        indicadores.DesviacionMargenPolitica.Should().Be(0.05m);
        indicadores.RutasSinMargenPolitica.Should().Be(1);
    }

    [Fact]
    public void UnaRutaSinMargenAplicadoNoSeCotizoYNoEntraNiSeCuentaComoSinPolitica()
    {
        var indicadores = Indicadores(
            Cotizacion(Ruta(1, 1_000_000m, 0.20m, 0.15m)),
            Cotizacion(Ruta(1, null, null, 0.30m), Ruta(2, null, null, null)));

        indicadores.MargenPoliticaPromedio.Should().Be(0.15m, "la política de una ruta sin costear no es de una ruta cotizada");
        indicadores.RutasSinMargenPolitica.Should().Be(0);
    }

    [Fact]
    public void SinCostoLaRutaEntraEnElSimplePeroNoEnElPonderadoNiEnLaDesviacion()
    {
        var indicadores = Indicadores(
            Cotizacion(Ruta(1, 2_000_000m, 0.20m, 0.10m)),
            Cotizacion(Ruta(1, null, 0.15m, 0.30m, precio: 100m)),
            Cotizacion(Ruta(1, 0m, 0.15m, 0.50m, precio: 100m)));

        indicadores.MargenPoliticaPromedio.Should().Be(0.3m, "(0,10 + 0,30 + 0,50) / 3");
        indicadores.MargenPoliticaPonderado.Should().Be(0.1m, "solo la ruta con costo > 0");
        indicadores.DesviacionMargenPolitica.Should().Be(0.1m);
    }

    [Fact]
    public void SinRutasConCostoElPonderadoYLaDesviacionSonNull()
    {
        var indicadores = Indicadores(Cotizacion(Ruta(1, null, 0.15m, 0.20m, precio: 100m)));

        indicadores.MargenPoliticaPromedio.Should().Be(0.2m);
        indicadores.MargenPoliticaPonderado.Should().BeNull();
        indicadores.DesviacionMargenPolitica.Should().BeNull();
    }

    [Fact]
    public void SinCotizacionesTodoEsNullYCeroRutas()
    {
        var indicadores = Indicadores();

        indicadores.MargenPoliticaPromedio.Should().BeNull();
        indicadores.MargenPoliticaPonderado.Should().BeNull();
        indicadores.DesviacionMargenPolitica.Should().BeNull();
        indicadores.RutasSinMargenPolitica.Should().Be(0);
    }

    [Fact]
    public void LaMonedaNoCambiaLaComparacionPorqueElCostoSiempreEstaEnPesos()
    {
        var enPesos = Indicadores(Cotizacion(Ruta(1, 1_000_000m, 0.20m, 0.15m), Ruta(2, 3_000_000m, 0.10m, 0.15m)));
        var enDolares = Indicadores(Cotizacion("USD", 4000m, Ruta(1, 1_000_000m, 0.20m, 0.15m, precio: 312.5m), Ruta(2, 3_000_000m, 0.10m, 0.15m, precio: 833.33m)));
        var enDolaresSinTrm = Indicadores(Cotizacion("USD", 0m, Ruta(1, 1_000_000m, 0.20m, 0.15m, precio: 312.5m), Ruta(2, 3_000_000m, 0.10m, 0.15m, precio: 833.33m)));

        foreach (var indicadores in new[] { enDolares, enDolaresSinTrm })
        {
            (indicadores.MargenPoliticaPromedio, indicadores.MargenPoliticaPonderado, indicadores.DesviacionMargenPolitica)
                .Should().Be((enPesos.MargenPoliticaPromedio, enPesos.MargenPoliticaPonderado, enPesos.DesviacionMargenPolitica));
        }

        enPesos.DesviacionMargenPolitica.Should().Be(-0.025m, "(0,05 × 1M − 0,05 × 3M) / 4M");
    }

    [Fact]
    public void LaDesviacionNegociadaDeAdr016NoCambia()
    {
        var conAjuste = Cotizacion(Ruta(1, 1_000_000m, 0.20m, 0.15m)) with { NumAjustes = 1, NumDiferencias = 1, SumaDiferencias = 0.05m };

        var indicadores = Indicadores(conAjuste, Cotizacion(Ruta(1, 1_000_000m, 0.10m, 0.15m)));

        indicadores.DesviacionPromedioMargen.Should().Be(0.05m, "sigue promediando solo los ajustes negociados (ADR-016)");
        indicadores.DesviacionMargenPolitica.Should().Be(0m, "(0,05 × 1M − 0,05 × 1M) / 2M");
    }
}
