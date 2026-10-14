using Cotifly.Dominio.Metricas;

namespace Cotifly.Dominio.Tests.Metricas;

/// <summary>
/// HU-021.10 (#137), decisión 8 del 2026-10-08: los conteos del tablero son por cotización; los valores y la apertura por línea de
/// negocio, por ruta (<see cref="clsRutaMetrica"/>).
/// </summary>
public sealed class clsCalculadoraMetricasRutasTests
{
    private static readonly Guid _ana = Guid.NewGuid();
    private static readonly DateTimeOffset _marzo = new(2026, 3, 10, 12, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset _ahora = new(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(-5));

    private static clsRutaMetrica Ruta(
        int orden, int? lineaId, string? linea, decimal? costo, decimal? precio, decimal? margen,
        int ajustes = 0, int diferencias = 0, decimal? sumaDiferencias = null) => new()
        {
            Orden = orden,
            LineaNegocioId = lineaId,
            LineaNegocio = linea,
            Costo = costo,
            PrecioFinal = precio,
            MargenAplicado = margen,
            NumAjustes = ajustes,
            NumDiferencias = diferencias,
            SumaDiferencias = sumaDiferencias,
        };

    /// <summary>Cotización con sus rutas; la cabecera suma el costo y el precio de las rutas como hace el repositorio.</summary>
    private static clsCotizacionMetrica Cotizacion(bool? ganada, string moneda, decimal trm, params clsRutaMetrica[] rutas) => new()
    {
        UsuarioId = _ana,
        CreatedAt = _marzo,
        CreatedAtVigente = _marzo,
        Ganada = ganada,
        Moneda = moneda,
        TrmAplicada = trm,
        Costo = rutas.All(r => r.Costo.HasValue) ? rutas.Sum(r => r.Costo!.Value) : null,
        PrecioFinal = rutas.All(r => r.PrecioFinal.HasValue) ? rutas.Sum(r => r.PrecioFinal!.Value) : null,
        NumAjustes = rutas.Sum(r => r.NumAjustes),
        NumDiferencias = rutas.Sum(r => r.NumDiferencias),
        SumaDiferencias = rutas.Sum(r => r.SumaDiferencias ?? 0m),
        Rutas = rutas,
    };

    /// <summary>Una cotización ganada con tres rutas de tres líneas distintas.</summary>
    private static clsCotizacionMetrica TresLineasGanada() => Cotizacion(
        true, "COP", 0m,
        Ruta(1, 1, "Nacional", 1_000_000m, 1_200_000m, 0.1667m, ajustes: 1, diferencias: 1, sumaDiferencias: -0.02m),
        Ruta(2, 2, "Importacion", 2_000_000m, 2_500_000m, 0.20m),
        Ruta(3, 3, "Exportacion", 3_000_000m, 3_300_000m, 0.0909m, ajustes: 2, diferencias: 2, sumaDiferencias: 0.03m));

    [Fact]
    public void UnaCotizacionConTresRutasDeDistintasLineasCuentaUnaVezEnLosConteos()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([TresLineasGanada()], _ahora, deadlineDisponible: false);

        indicadores.Cantidad.Should().Be(1, "los conteos son por cotización, no por ruta");
        indicadores.Ganadas.Should().Be(1);
        indicadores.Conversion.Should().Be(1m);
        indicadores.CosteoTotal.Should().Be(6_000_000m, "el costo de la cotización es la suma de sus rutas");
        indicadores.ValorGanadoCop.Should().Be(7_000_000m);
        indicadores.Ganancias.Should().Be(1_000_000m);
        indicadores.AjustesMargen.Should().Be(3, "los ajustes de todas sus rutas");
    }

    [Fact]
    public void LaAperturaPorLineaEsPorRutaYLaCotizacionApareceEnCadaLinea()
    {
        var apertura = clsCalculadoraMetricas.PorLineaNegocio([TresLineasGanada()]);

        apertura.Should().Equal(
            new clsMetricaLineaNegocio(3, "Exportacion", 1, 1, 0, 1, 3_000_000m, 3_000_000m, 3_300_000m, 3_300_000m, 300_000m, 0.0909m, 2, 0.015m),
            new clsMetricaLineaNegocio(2, "Importacion", 1, 1, 0, 1, 2_000_000m, 2_000_000m, 2_500_000m, 2_500_000m, 500_000m, 0.20m, 0, null),
            new clsMetricaLineaNegocio(1, "Nacional", 1, 1, 0, 1, 1_000_000m, 1_000_000m, 1_200_000m, 1_200_000m, 200_000m, 0.1667m, 1, -0.02m));
        apertura.Sum(l => l.Cotizaciones).Should().Be(3, "la suma de la apertura puede superar la cantidad (1)");
        apertura.Sum(l => l.CosteoTotal).Should().Be(6_000_000m, "los valores de la apertura sí cuadran con el total");
    }

    [Fact]
    public void ElMargenPonderadoEsSobreElPrecioDeTodasLasRutas()
    {
        var filas = new[]
        {
            TresLineasGanada(),
            Cotizacion(false, "COP", 0m, Ruta(1, 1, "Nacional", 4_000_000m, 4_400_000m, 0.10m)),
            Cotizacion(null, "COP", 0m, Ruta(1, 1, "Nacional", null, null, null)),
        };

        var indicadores = clsCalculadoraMetricas.Indicadores(filas, _ahora, deadlineDisponible: false);

        // Σ(precio − costo) / Σ precio = (11,4M − 10M) / 11,4M = 0,122807…; la ruta sin costo ni precio no entra.
        indicadores.MargenPonderado.Should().Be(0.1228m);
    }

    [Fact]
    public void DosCotizacionesEnLaMismaLineaCuentanDosYSusRutasSeSuman()
    {
        var filas = new[]
        {
            Cotizacion(true, "COP", 0m, Ruta(1, 1, "Nacional", 1_000_000m, 1_100_000m, 0.10m), Ruta(2, 1, "Nacional", 1_000_000m, 1_200_000m, 0.20m)),
            Cotizacion(false, "COP", 0m, Ruta(1, 1, "Nacional", 2_000_000m, 2_400_000m, 0.20m)),
            Cotizacion(null, "COP", 0m, Ruta(1, null, null, 500_000m, null, null)),
        };

        var apertura = clsCalculadoraMetricas.PorLineaNegocio(filas);

        apertura.Should().HaveCount(2);
        apertura[0].Should().BeEquivalentTo(new
        {
            LineaNegocioId = (int?)1,
            Cotizaciones = 2,
            Ganadas = 1,
            Perdidas = 1,
            Rutas = 3,
            CosteoTotal = 4_000_000m,
            CosteoGanadas = 2_000_000m,
            ValorCotizadoCop = 4_700_000m,
            ValorGanadoCop = 2_300_000m,
            Ganancias = 300_000m,
            MargenPonderado = (decimal?)0.1489m, // (4,7M − 4M) / 4,7M
        });
        apertura[1].Should().BeEquivalentTo(new { LineaNegocioId = (int?)null, LineaNegocio = (string?)null, Cotizaciones = 1, Rutas = 1, ValorCotizadoCop = 0m },
            "las rutas sin línea van al final");
    }

    [Fact]
    public void EnUsdCadaRutaSeConvierteConLaTrmDeSuCotizacionYElCostoNo()
    {
        var usd = Cotizacion(
            true, "USD", 4_000.5m,
            Ruta(1, 1, "Nacional", 3_000_000m, 1_000.01m, 0.25m),
            Ruta(2, 2, "Importacion", 1_000_000m, 300.33m, 0.20m));

        var apertura = clsCalculadoraMetricas.PorLineaNegocio([usd]);
        var indicadores = clsCalculadoraMetricas.Indicadores([usd], _ahora, deadlineDisponible: false);

        // 1.000,01 × 4.000,5 = 4.000.540,005 → 4.000.540,00 (half-to-even); 300,33 × 4.000,5 = 1.201.470,165 → 1.201.470,16.
        apertura.Single(l => l.LineaNegocioId == 1).ValorCotizadoCop.Should().Be(4_000_540.00m);
        apertura.Single(l => l.LineaNegocioId == 1).CosteoTotal.Should().Be(3_000_000m, "el costo está en COP y nunca se convierte");
        apertura.Single(l => l.LineaNegocioId == 1).Ganancias.Should().Be(1_000_540.00m);
        apertura.Single(l => l.LineaNegocioId == 2).ValorCotizadoCop.Should().Be(1_201_470.16m);
        // La cabecera: 1.300,34 × 4.000,5 = 5.202.010,17. La apertura suma 5.202.010,16: un centavo de diferencia por redondear por ruta.
        indicadores.ValorCotizadoCop.Should().Be(5_202_010.17m);
        indicadores.CotizacionesUsd.Should().Be(1);
    }

    [Fact]
    public void UnaRutaEnUsdSinTrmNoSumaDinero()
    {
        var apertura = clsCalculadoraMetricas.PorLineaNegocio([Cotizacion(true, "USD", 0m, Ruta(1, 1, "Nacional", 3_000_000m, 1_000m, 0.25m))]);

        apertura.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Cotizaciones = 1, ValorCotizadoCop = 0m, Ganancias = 0m, CosteoTotal = 3_000_000m });
    }

    [Fact]
    public void SinRutasLaAperturaEstaVaciaYElMargenPonderadoEsNulo()
    {
        var sinRutas = new clsCotizacionMetrica { UsuarioId = _ana, CreatedAt = _marzo, PrecioFinal = 1m };

        clsCalculadoraMetricas.PorLineaNegocio([sinRutas]).Should().BeEmpty();
        clsCalculadoraMetricas.Indicadores([sinRutas], _ahora, deadlineDisponible: false).MargenPonderado.Should().BeNull();
    }

    /// <summary>
    /// B3 (#173, observación del validador): la -v2 vigente no tiene precio y la v1 ganada sí. La apertura por línea de negocio saca lo
    /// ganado de las rutas de la v1 (con su moneda y su TRM), igual que los indicadores; antes daba 0.
    /// </summary>
    [Fact]
    public void LaAperturaPorLineaSacaLoGanadoDeLaVersionGanada()
    {
        var cadena = Cotizacion(true, "COP", 0m, Ruta(1, 7, "Nacional", null, null, null)) with
        {
            VersionResultado = new clsVersionResultadoMetrica(1_000m, "USD", 4_000m, 3_000_000m),
            RutasResultado = [Ruta(1, 7, "Nacional", 3_000_000m, 1_000m, 0.25m)],
        };

        var linea = clsCalculadoraMetricas.PorLineaNegocio([cadena]).Should().ContainSingle().Subject;
        var indicadores = clsCalculadoraMetricas.Indicadores([cadena], _ahora, deadlineDisponible: false);

        linea.Ganadas.Should().Be(1);
        linea.ValorCotizadoCop.Should().Be(0m, "lo cotizado es de la vigente");
        linea.ValorGanadoCop.Should().Be(4_000_000m, "1.000 USD × 4.000 de la versión ganada");
        linea.CosteoGanadas.Should().Be(3_000_000m);
        linea.Ganancias.Should().Be(1_000_000m);
        linea.ValorGanadoCop.Should().Be(indicadores.ValorGanadoCop, "la apertura cuadra con el total");
    }
}
