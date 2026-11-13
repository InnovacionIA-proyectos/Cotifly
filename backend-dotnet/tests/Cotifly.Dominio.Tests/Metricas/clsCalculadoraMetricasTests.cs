using Cotifly.Dominio.Metricas;

namespace Cotifly.Dominio.Tests.Metricas;

/// <summary>Definiciones de los indicadores del tablero (ADR-016 §4), una por prueba.</summary>
public sealed class clsCalculadoraMetricasTests
{
    private static readonly Guid _ana = Guid.NewGuid();
    private static readonly TimeSpan _bogota = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset _marzo = new(2026, 3, 10, 12, 0, 0, _bogota);
    private static readonly DateTimeOffset _ahora = new(2026, 9, 29, 12, 0, 0, _bogota);

    private static clsCotizacionMetrica Fila(
        decimal? precio = 1_000_000m,
        decimal? costo = 850_000m,
        bool? ganada = null,
        string? moneda = "COP",
        decimal trm = 0m,
        decimal? margen = 0.15m,
        DateTimeOffset? creada = null,
        int versiones = 1) => new()
        {
            UsuarioId = _ana,
            CreatedAt = creada ?? _marzo,
            CreatedAtVigente = creada ?? _marzo,
            Versiones = versiones,
            PrecioFinal = precio,
            Costo = costo,
            Ganada = ganada,
            Moneda = moneda,
            TrmAplicada = trm,
            MargenAplicado = margen,
        };

    private static clsIndicadoresMetricas Indicadores(params clsCotizacionMetrica[] filas) =>
        clsCalculadoraMetricas.Indicadores(filas, _ahora, deadlineDisponible: false);

    private static DateOnly FechaBogota(DateTimeOffset instante) => DateOnly.FromDateTime(instante.ToOffset(_bogota).DateTime);

    [Fact]
    public void SinCotizacionesTodoEsCeroYLosPromediosNulos()
    {
        var indicadores = Indicadores();

        indicadores.Should().BeEquivalentTo(new clsIndicadoresMetricas());
        indicadores.Conversion.Should().BeNull();
        indicadores.ConversionSobreCerradas.Should().BeNull();
        indicadores.MargenPromedio.Should().BeNull();
        indicadores.TiempoPromedioCotizacionHoras.Should().BeNull();
        indicadores.DesviacionPromedioMargen.Should().BeNull();
        indicadores.DesviacionPromedioMargenAbs.Should().BeNull();
        indicadores.DeadlineVencidas.Should().BeNull("sin la columna del 0004");
    }

    [Fact]
    public void ElUniversoSeParteEnGanadasPerdidasSinResultadoYPendientes()
    {
        var indicadores = Indicadores(
            Fila(ganada: true), Fila(ganada: true), Fila(ganada: false), Fila(ganada: null), Fila(ganada: null, precio: null),
            Fila(ganada: true, precio: null));

        indicadores.Cantidad.Should().Be(6);
        indicadores.Ganadas.Should().Be(3, "una ganada sin precio cuenta por su resultado");
        indicadores.Perdidas.Should().Be(1);
        indicadores.SinResultado.Should().Be(1, "con precio y sin resultado");
        indicadores.PendientesPorCotizar.Should().Be(1, "sin precio y sin resultado");
        (indicadores.Ganadas + indicadores.Perdidas + indicadores.SinResultado + indicadores.PendientesPorCotizar)
            .Should().Be(indicadores.Cantidad);
        indicadores.Conversion.Should().Be(0.5m, "ganadas / cantidad (backlog HU-010)");
        indicadores.ConversionSobreCerradas.Should().Be(0.75m, "ganadas / (ganadas + perdidas)");
    }

    [Fact]
    public void ConvierteUsdConLaTrmYNuncaMultiplicaElCostoNiLasDeCop()
    {
        var indicadores = Indicadores(
            Fila(precio: 1_000_000m, costo: 850_000m, ganada: true),
            Fila(precio: 1_000m, costo: 3_400_000m, ganada: true, moneda: "USD", trm: 4_000m),
            Fila(precio: 2_000_000m, costo: 1_700_000m, ganada: false));

        indicadores.ValorCotizadoCop.Should().Be(7_000_000m);
        indicadores.ValorGanadoCop.Should().Be(5_000_000m);
        indicadores.CosteoTotal.Should().Be(5_950_000m, "el costo ya está en COP y suma todo el universo");
        indicadores.CosteoGanadas.Should().Be(4_250_000m);
        indicadores.Ganancias.Should().Be(750_000m, "(1.000.000 − 850.000) + (4.000.000 − 3.400.000), solo ganadas");
        (indicadores.ValorGanadoCop - indicadores.CosteoGanadas).Should().Be(indicadores.Ganancias);
        indicadores.CotizacionesUsd.Should().Be(1);
    }

    [Fact]
    public void ElValorEnUsdSeRedondeaPorFilaADosDecimalesHalfToEven()
    {
        Fila(precio: 0.125m, costo: null, moneda: "USD", trm: 1m).ValorCop.Should().Be(0.12m);
        Fila(precio: 0.135m, costo: null, moneda: "usd", trm: 1m).ValorCop.Should().Be(0.14m);
    }

    [Fact]
    public void UnaCotizacionEnUsdSinTrmCuentaPeroNoSumaDinero()
    {
        var indicadores = Indicadores(Fila(precio: 1_000m, moneda: "USD", trm: 0m, ganada: true));

        indicadores.Cantidad.Should().Be(1);
        indicadores.ValorCotizadoCop.Should().Be(0m);
        indicadores.Ganancias.Should().Be(0m);
    }

    [Fact]
    public void LaMonedaNulaSeTrataComoPesos() => Fila(precio: 500m, moneda: null).ValorCop.Should().Be(500m);

    [Fact]
    public void VersionesSumaLasFilasDeCadaCadena()
    {
        var indicadores = Indicadores(Fila(versiones: 3), Fila());

        indicadores.Cantidad.Should().Be(2);
        indicadores.Versiones.Should().Be(4);
    }

    [Fact]
    public void ElMargenPromedioEsSimpleYOmiteLosNulos() =>
        Indicadores(Fila(margen: 0.10m), Fila(margen: 0.15m), Fila(margen: 0.2m), Fila(margen: null)).MargenPromedio.Should().Be(0.15m);

    [Fact]
    public void ElTiempoPromedioVaDeLaVersionVigenteACosteoListoYDescartaLosNegativos()
    {
        var conCosteo = Fila() with { CosteoListoAt = _marzo.AddHours(3) };
        var otra = Fila() with { CosteoListoAt = _marzo.AddHours(4.5) };
        var incoherente = Fila() with { CosteoListoAt = _marzo.AddHours(-10) };

        Indicadores(conCosteo, otra, incoherente, Fila()).TiempoPromedioCotizacionHoras.Should().Be(3.75m);
    }

    [Fact]
    public void LasDesviacionesPromedianConSignoYEnValorAbsoluto()
    {
        var uno = Fila() with { NumAjustes = 2, NumDiferencias = 2, SumaDiferencias = -0.08m, SumaDiferenciasAbsolutas = 0.08m };
        var otro = Fila() with { NumAjustes = 2, NumDiferencias = 1, SumaDiferencias = 0.05m, SumaDiferenciasAbsolutas = 0.05m };

        var indicadores = Indicadores(uno, otro);

        indicadores.AjustesMargen.Should().Be(4);
        indicadores.DesviacionPromedioMargen.Should().Be(-0.01m);
        indicadores.DesviacionPromedioMargenAbs.Should().Be(0.0433m);
    }

    [Fact]
    public void DeadlineVencidasCuentaLasSinResultadoConFechaPasada()
    {
        var filas = new[]
        {
            Fila() with { Deadline = _ahora.AddDays(-1) },
            Fila(precio: null) with { Deadline = _ahora.AddHours(-1) },
            Fila(ganada: true) with { Deadline = _ahora.AddDays(-1) },
            Fila() with { Deadline = _ahora.AddDays(1) },
            Fila(),
        };

        clsCalculadoraMetricas.Indicadores(filas, _ahora, deadlineDisponible: true).DeadlineVencidas.Should().Be(2);
        clsCalculadoraMetricas.Indicadores(filas, _ahora, deadlineDisponible: false).DeadlineVencidas.Should().BeNull();
    }

    [Fact]
    public void LaSerieTraeTodosLosMesesDelPeriodoEnFechaDeBogota()
    {
        // 1 de abril 03:00 UTC es todavía 31 de marzo en Bogotá.
        var finDeMarzo = Fila(ganada: true, creada: new DateTimeOffset(2026, 4, 1, 3, 0, 0, TimeSpan.Zero));
        var mayo = Fila(ganada: false, creada: new DateTimeOffset(2026, 5, 20, 12, 0, 0, _bogota));

        var serie = clsCalculadoraMetricas.SerieMensual([finDeMarzo, mayo], new DateOnly(2026, 3, 15), new DateOnly(2026, 5, 2), FechaBogota);

        serie.Select(p => p.Mes).Should().Equal("2026-03", "2026-04", "2026-05");
        serie[0].Should().Be(new clsPuntoSerieMensual("2026-03", 1, 1, 0, 1_000_000m, 1_000_000m, 850_000m, 850_000m, 150_000m));
        serie[1].Should().Be(new clsPuntoSerieMensual("2026-04", 0, 0, 0, 0m, 0m, 0m, 0m, 0m));
        serie[2].Should().Be(new clsPuntoSerieMensual("2026-05", 1, 0, 1, 1_000_000m, 0m, 850_000m, 0m, 0m));
    }

    [Fact]
    public void LaConcentracionAgrupaPorDestinoYOrigenSinDistinguirMayusculas()
    {
        var filas = new[]
        {
            Fila(ganada: true) with { Origen = "MEDELLIN", Destino = "BOGOTA D.C." },
            Fila(ganada: false) with { Origen = "Medellin ", Destino = "bogota d.c." },
            Fila(ganada: true, precio: 500_000m) with { Origen = "CALI", Destino = "CARTAGENA" },
            Fila() with { Origen = null, Destino = null },
        };

        var concentracion = clsCalculadoraMetricas.Concentracion(filas, disponible: true);

        concentracion.Disponible.Should().BeTrue();
        concentracion.PorDestino.Should().Equal(
            new clsMetricaCiudad("BOGOTA D.C.", 2, 1, 2_000_000m, 1_000_000m),
            new clsMetricaCiudad("CARTAGENA", 1, 1, 500_000m, 500_000m));
        concentracion.PorOrigen.Should().Equal(
            new clsMetricaCiudad("MEDELLIN", 2, 1, 2_000_000m, 1_000_000m),
            new clsMetricaCiudad("CALI", 1, 1, 500_000m, 500_000m));
        concentracion.SinDestino.Should().Be(1);
        concentracion.SinOrigen.Should().Be(1);
    }

    [Fact]
    public void SinColumnasDeCiudadLaConcentracionNoEstaDisponible() =>
        clsCalculadoraMetricas.Concentracion([Fila(), Fila()], disponible: false)
            .Should().BeEquivalentTo(new clsConcentracionCiudades(false, [], [], 2, 2));

    [Fact]
    public void B3LoGanadoSaleDeLaVersionGanadaYNoDeLaVigente()
    {
        // La -v2 vigente no tiene precio; la v1 (ganada) costó 4.000.000 y se cotizó en 5.000.000.
        var cadena = Fila(precio: null, costo: null, ganada: true, versiones: 2) with
        {
            VersionResultado = new clsVersionResultadoMetrica(5_000_000m, "COP", 0m, 4_000_000m),
        };
        var enDolares = Fila(precio: null, costo: null, ganada: true) with
        {
            VersionResultado = new clsVersionResultadoMetrica(1_000m, "USD", 4_000m, 3_000_000m),
        };

        var indicadores = Indicadores(cadena, enDolares);

        indicadores.ValorCotizadoCop.Should().Be(0m, "lo cotizado sigue siendo lo de la vigente");
        indicadores.ValorGanadoCop.Should().Be(9_000_000m);
        indicadores.CosteoGanadas.Should().Be(7_000_000m);
        indicadores.Ganancias.Should().Be(2_000_000m);
        clsCalculadoraMetricas.SerieMensual([cadena], new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), f => DateOnly.FromDateTime(f.UtcDateTime))
            .Sum(p => p.ValorGanadoCop).Should().Be(5_000_000m);
    }

    [Fact]
    public void CadaMesTraeSusGananciasYSuCosteoConLaReglaDeLosIndicadores()
    {
        var abril = new DateTimeOffset(2026, 4, 10, 12, 0, 0, _bogota);
        var marzo = new[]
        {
            Fila(precio: 1_000_000m, costo: 850_000m, ganada: true),
            Fila(precio: 1_000m, costo: 3_400_000m, ganada: true, moneda: "USD", trm: 4_000m),
            Fila(precio: 2_000_000m, costo: 1_700_000m, ganada: false),
        };
        var deAbril = new[]
        {
            // En USD sin TRM: su costo cuenta, su valor no (ni su ganancia).
            Fila(precio: 1_000m, costo: 500_000m, ganada: true, moneda: "USD", trm: 0m, creada: abril),
            // Ganada sin costo: suma valor ganado, no ganancia.
            Fila(precio: 300_000m, costo: null, ganada: true, creada: abril),
            // Lo ganado sale de la versión ganada, no de la vigente sin precio ni costo.
            Fila(precio: null, costo: null, ganada: true, creada: abril, versiones: 2) with
            {
                VersionResultado = new clsVersionResultadoMetrica(5_000_000m, "COP", 0m, 4_000_000m),
            },
        };

        var serie = clsCalculadoraMetricas.SerieMensual([.. marzo, .. deAbril], new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30), FechaBogota);

        serie.Should().Equal(
            new clsPuntoSerieMensual("2026-03", 3, 2, 1, 7_000_000m, 5_000_000m, 5_950_000m, 4_250_000m, 750_000m),
            new clsPuntoSerieMensual("2026-04", 3, 3, 0, 300_000m, 5_300_000m, 500_000m, 4_500_000m, 1_000_000m));
        var deMarzo = Indicadores(marzo);
        (deMarzo.CosteoTotal, deMarzo.CosteoGanadas, deMarzo.Ganancias).Should().Be((serie[0].CosteoTotal, serie[0].CosteoGanadas, serie[0].Ganancias),
            "un mes de la serie es lo que darían los indicadores de ese mes");
    }

    [Fact]
    public void LaSumaDeLosMesesCuadraConLosIndicadoresDelPeriodo()
    {
        // Montos de 2 decimales como en la base (DECIMAL(18,2)) y valores en USD redondeados por fila: la suma no se descuadra.
        var azar = new Random(201);
        decimal? Monto(int maximo) => azar.Next(10) == 0 ? null : azar.Next(1, maximo) + azar.Next(100) / 100m;
        bool? Resultado() => azar.Next(3) switch { 0 => true, 1 => false, _ => null };
        var filas = Enumerable.Range(0, 400).Select(_ =>
        {
            var usd = azar.Next(4) == 0;
            var fila = Fila(
                precio: Monto(usd ? 10_000 : 50_000_000),
                costo: Monto(40_000_000),
                ganada: Resultado(),
                moneda: usd ? "USD" : "COP",
                trm: usd && azar.Next(10) > 0 ? 3_800m + azar.Next(100_000) / 100m : 0m,
                creada: new DateTimeOffset(2026, 1, 1, 0, 0, 0, _bogota).AddMinutes(azar.Next(0, 365 * 24 * 60)));
            return azar.Next(5) == 0 && fila.Ganada == true
                ? fila with { VersionResultado = new clsVersionResultadoMetrica(Monto(50_000_000), "COP", 0m, Monto(40_000_000)) }
                : fila;
        }).ToList();

        var serie = clsCalculadoraMetricas.SerieMensual(filas, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), FechaBogota);
        var total = Indicadores([.. filas]);

        serie.Should().HaveCount(12);
        new
        {
            Cantidad = serie.Sum(p => p.Cantidad),
            Ganadas = serie.Sum(p => p.Ganadas),
            Perdidas = serie.Sum(p => p.Perdidas),
            ValorCotizadoCop = serie.Sum(p => p.ValorCotizadoCop),
            ValorGanadoCop = serie.Sum(p => p.ValorGanadoCop),
            CosteoTotal = serie.Sum(p => p.CosteoTotal),
            CosteoGanadas = serie.Sum(p => p.CosteoGanadas),
            Ganancias = serie.Sum(p => p.Ganancias),
        }.Should().BeEquivalentTo(new
        {
            total.Cantidad,
            total.Ganadas,
            total.Perdidas,
            total.ValorCotizadoCop,
            total.ValorGanadoCop,
            total.CosteoTotal,
            total.CosteoGanadas,
            total.Ganancias,
        });
        total.Ganancias.Should().NotBe(0m);
    }

    [Fact]
    public void DiciembreYEneroDelAnioSiguienteSonMesesDistintosEnHoraDeBogota()
    {
        // 23:30 del 31 de diciembre en Bogotá ya es 1 de enero en UTC: cuenta en diciembre. 00:30 del 1 de enero cuenta en enero.
        var diciembre = Fila(precio: 1_000_000m, costo: 900_000m, ganada: true, creada: new DateTimeOffset(2025, 12, 31, 23, 30, 0, _bogota));
        var enero = Fila(precio: 2_000_000m, costo: 1_500_000m, ganada: true, creada: new DateTimeOffset(2026, 1, 1, 0, 30, 0, _bogota));
        var eneroPerdida = Fila(precio: 700_000m, costo: 600_000m, ganada: false, creada: new DateTimeOffset(2026, 1, 31, 23, 59, 0, _bogota));

        var serie = clsCalculadoraMetricas.SerieMensual([enero, diciembre, eneroPerdida], new DateOnly(2025, 12, 1), new DateOnly(2026, 1, 31), FechaBogota);

        serie.Should().Equal(
            new clsPuntoSerieMensual("2025-12", 1, 1, 0, 1_000_000m, 1_000_000m, 900_000m, 900_000m, 100_000m),
            new clsPuntoSerieMensual("2026-01", 2, 1, 1, 2_700_000m, 2_000_000m, 2_100_000m, 1_500_000m, 500_000m));
    }

    [Fact]
    public void UnMesConGanadasPorDebajoDelCostoTieneGananciasNegativas()
    {
        // Una ganada vendida por debajo del costo resta: la serie no recorta en 0 (como indicadores.ganancias).
        var serie = clsCalculadoraMetricas.SerieMensual(
            [Fila(precio: 800_000m, costo: 1_000_000m, ganada: true), Fila(precio: 1_000_000m, costo: 900_000m, ganada: true)],
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), FechaBogota);

        serie.Single().Ganancias.Should().Be(-100_000m);
    }

    [Fact]
    public void MontosEnElTopeDeLaBaseNoSeDesbordanYCuadran()
    {
        // DECIMAL(18,2): 9.999.999.999.999.999,99 por fila; en USD, × una TRM de 5.000. 600 filas en 12 meses.
        const decimal Tope = 9_999_999_999_999_999.99m;
        var filas = Enumerable.Range(0, 600).Select(i =>
        {
            var creada = new DateTimeOffset(2026, (i % 12) + 1, 15, 12, 0, 0, _bogota);
            return i % 3 == 0
                ? Fila(precio: Tope, costo: Tope - 0.01m, ganada: true, moneda: "USD", trm: 5_000.25m, creada: creada)
                : Fila(precio: Tope, costo: Tope, ganada: i % 3 == 1, creada: creada);
        }).ToArray();

        var serie = clsCalculadoraMetricas.SerieMensual(filas, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), FechaBogota);
        var total = Indicadores(filas);

        serie.Sum(p => p.CosteoTotal).Should().Be(total.CosteoTotal).And.Be((600 * Tope) - (200 * 0.01m));
        serie.Sum(p => p.CosteoGanadas).Should().Be(total.CosteoGanadas);
        serie.Sum(p => p.ValorCotizadoCop).Should().Be(total.ValorCotizadoCop);
        serie.Sum(p => p.ValorGanadoCop).Should().Be(total.ValorGanadoCop);
        serie.Sum(p => p.Ganancias).Should().Be(total.Ganancias)
            .And.Be(200 * (Math.Round(Tope * 5_000.25m, 2, MidpointRounding.ToEven) - (Tope - 0.01m)), "las COP ganadas no ganan nada; las USD ganan precio × TRM − costo");
    }

    [Fact]
    public void ElRefactorAMontosDeNoCambiaLosIndicadores()
    {
        // Las fórmulas de los indicadores escritas a mano, contra filas al azar (USD con y sin TRM, ganada en una versión anterior, sin costo).
        var azar = new Random(3401);
        decimal? Monto() => azar.Next(8) == 0 ? null : azar.Next(1, 90_000_000) + (azar.Next(100) / 100m);
        var filas = Enumerable.Range(0, 300).Select(_ =>
        {
            var usd = azar.Next(3) == 0;
            var fila = Fila(
                precio: Monto(),
                costo: Monto(),
                ganada: azar.Next(3) switch { 0 => true, 1 => false, _ => null },
                moneda: usd ? "USD" : "COP",
                trm: usd && azar.Next(5) > 0 ? 3_900m + (azar.Next(50_000) / 100m) : 0m);
            return fila.Ganada == true && azar.Next(4) == 0
                ? fila with { VersionResultado = new clsVersionResultadoMetrica(Monto(), usd ? "USD" : "COP", usd ? 4_100.37m : 0m, Monto()) }
                : fila;
        }).ToArray();
        var ganadas = filas.Where(f => f.Ganada == true).ToList();

        var indicadores = Indicadores(filas);

        indicadores.CosteoTotal.Should().Be(Math.Round(filas.Sum(f => f.Costo ?? 0m), 2, MidpointRounding.ToEven));
        indicadores.CosteoGanadas.Should().Be(Math.Round(ganadas.Sum(f => f.CostoResultado ?? 0m), 2, MidpointRounding.ToEven));
        indicadores.ValorCotizadoCop.Should().Be(Math.Round(filas.Sum(f => f.ValorCop ?? 0m), 2, MidpointRounding.ToEven));
        indicadores.ValorGanadoCop.Should().Be(Math.Round(ganadas.Sum(f => f.ValorResultadoCop ?? 0m), 2, MidpointRounding.ToEven));
        indicadores.Ganancias.Should().Be(Math.Round(
            ganadas.Where(f => f.ValorResultadoCop.HasValue && f.CostoResultado.HasValue).Sum(f => f.ValorResultadoCop!.Value - f.CostoResultado!.Value),
            2,
            MidpointRounding.ToEven));
        indicadores.Ganancias.Should().NotBe(0m);
    }
}
