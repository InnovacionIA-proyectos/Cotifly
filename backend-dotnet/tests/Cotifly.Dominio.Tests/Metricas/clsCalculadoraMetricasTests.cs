using Cotifly.Dominio.Metricas;

namespace Cotifly.Dominio.Tests.Metricas;

/// <summary>Definiciones de los indicadores del tablero (HU-010.1, ADR-016 §4), una por prueba.</summary>
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
        serie[0].Should().Be(new clsPuntoSerieMensual("2026-03", 1, 1, 0, 1_000_000m, 1_000_000m));
        serie[1].Should().Be(new clsPuntoSerieMensual("2026-04", 0, 0, 0, 0m, 0m));
        serie[2].Should().Be(new clsPuntoSerieMensual("2026-05", 1, 0, 1, 1_000_000m, 0m));
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
}
