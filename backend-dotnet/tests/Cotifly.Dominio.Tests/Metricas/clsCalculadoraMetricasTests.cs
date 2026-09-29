using Cotifly.Dominio.Metricas;

namespace Cotifly.Dominio.Tests.Metricas;

/// <summary>Definiciones de los indicadores del tablero (HU-010.1, ADR-016 §4), una por prueba.</summary>
public sealed class clsCalculadoraMetricasTests
{
    private static readonly Guid _ana = Guid.NewGuid();
    private static readonly TimeSpan _bogota = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset _marzo = new(2026, 3, 10, 12, 0, 0, _bogota);

    private static clsCotizacionMetrica Fila(
        decimal? precio = 1_000_000m,
        decimal? costo = 850_000m,
        bool? ganada = null,
        string? moneda = "COP",
        decimal trm = 0m,
        decimal? margen = 0.15m,
        DateTimeOffset? creada = null,
        bool vigente = true) => new()
        {
            UsuarioId = _ana,
            CreatedAt = creada ?? _marzo,
            EsVigente = vigente,
            PrecioFinal = precio,
            Costo = costo,
            Ganada = ganada,
            Moneda = moneda,
            TrmAplicada = trm,
            MargenAplicado = margen,
        };

    private static DateOnly FechaBogota(DateTimeOffset instante) => DateOnly.FromDateTime(instante.ToOffset(_bogota).DateTime);

    [Fact]
    public void SinCotizacionesTodoEsCeroYLosPromediosNulos()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([]);

        indicadores.Should().BeEquivalentTo(new clsIndicadoresMetricas());
        indicadores.Conversion.Should().BeNull();
        indicadores.ConversionSobreCerradas.Should().BeNull();
        indicadores.MargenPromedio.Should().BeNull();
        indicadores.TiempoPromedioCotizacionHoras.Should().BeNull();
        indicadores.DesviacionPromedioMargen.Should().BeNull();
    }

    [Fact]
    public void CuentaResultadosYCalculaLasDosConversiones()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores(
        [
            Fila(ganada: true), Fila(ganada: true), Fila(ganada: false), Fila(ganada: null), Fila(ganada: null, precio: null),
        ]);

        indicadores.Cantidad.Should().Be(5);
        indicadores.Ganadas.Should().Be(2);
        indicadores.Perdidas.Should().Be(1);
        indicadores.SinResultado.Should().Be(2);
        indicadores.PendientesPorCotizar.Should().Be(1);
        indicadores.Conversion.Should().Be(0.4m, "ganadas / cantidad (backlog HU-010)");
        indicadores.ConversionSobreCerradas.Should().Be(0.6667m, "ganadas / (ganadas + perdidas), 4 decimales");
    }

    [Fact]
    public void ConvierteUsdConLaTrmYNuncaMultiplicaElCostoNiLasDeCop()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores(
        [
            Fila(precio: 1_000_000m, costo: 850_000m, ganada: true),
            Fila(precio: 1_000m, costo: 3_400_000m, ganada: true, moneda: "USD", trm: 4_000m),
            Fila(precio: 2_000_000m, costo: 1_700_000m, ganada: false),
        ]);

        indicadores.ValorCotizadoCop.Should().Be(7_000_000m);
        indicadores.ValorGanadoCop.Should().Be(5_000_000m);
        indicadores.CosteoTotal.Should().Be(5_950_000m, "el costo ya está en COP y suma todo el universo");
        indicadores.Ganancias.Should().Be(750_000m, "(1.000.000 − 850.000) + (4.000.000 − 3.400.000), solo ganadas");
        indicadores.CotizacionesUsd.Should().Be(1);
    }

    [Fact]
    public void ElPrecioEnUsdSeRedondeaPorFilaADosDecimalesHalfToEven()
    {
        // 0.125 × 1 = 0.125 → 0.12 (half-to-even); 0.135 → 0.14.
        var una = Fila(precio: 0.125m, costo: null, moneda: "USD", trm: 1m);
        var otra = Fila(precio: 0.135m, costo: null, moneda: "usd", trm: 1m);

        una.PrecioCop.Should().Be(0.12m);
        otra.PrecioCop.Should().Be(0.14m);
    }

    [Fact]
    public void UnaCotizacionEnUsdSinTrmCuentaPeroNoSumaDinero()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([Fila(precio: 1_000m, moneda: "USD", trm: 0m, ganada: true)]);

        indicadores.Cantidad.Should().Be(1);
        indicadores.ValorCotizadoCop.Should().Be(0m);
        indicadores.Ganancias.Should().Be(0m);
    }

    [Fact]
    public void LaMonedaNulaSeTrataComoPesos()
    {
        Fila(precio: 500m, moneda: null).PrecioCop.Should().Be(500m);
    }

    [Fact]
    public void SoloLaVersionVigenteEntraEnElUniversoPeroVersionesCuentaTodas()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores(
        [
            Fila(precio: 2_000_000m, vigente: false, ganada: false),
            Fila(precio: 2_100_000m, vigente: true, ganada: true),
        ]);

        indicadores.Cantidad.Should().Be(1);
        indicadores.Versiones.Should().Be(2);
        indicadores.ValorCotizadoCop.Should().Be(2_100_000m);
        indicadores.Perdidas.Should().Be(0);
    }

    [Fact]
    public void ElMargenPromedioEsSimpleYOmiteLosNulos()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([Fila(margen: 0.10m), Fila(margen: 0.15m), Fila(margen: 0.2m), Fila(margen: null)]);

        indicadores.MargenPromedio.Should().Be(0.15m);
    }

    [Fact]
    public void ElTiempoPromedioVaDeCreadaACosteoListoEnHoras()
    {
        var conCosteo = Fila() with { CosteoListoAt = _marzo.AddHours(3) };
        var otra = Fila() with { CosteoListoAt = _marzo.AddHours(4.5) };

        var indicadores = clsCalculadoraMetricas.Indicadores([conCosteo, otra, Fila()]);

        indicadores.TiempoPromedioCotizacionHoras.Should().Be(3.75m);
    }

    [Fact]
    public void LaDesviacionPromediaTodosLosAjustesDelUniverso()
    {
        var uno = Fila() with { NumAjustes = 2, NumDiferencias = 2, SumaDiferencias = -0.08m };
        var otro = Fila() with { NumAjustes = 1, NumDiferencias = 1, SumaDiferencias = -0.01m };
        var vieja = Fila(vigente: false) with { NumAjustes = 1, NumDiferencias = 1, SumaDiferencias = -0.5m };

        var indicadores = clsCalculadoraMetricas.Indicadores([uno, otro, vieja]);

        indicadores.AjustesMargen.Should().Be(3);
        indicadores.DesviacionPromedioMargen.Should().Be(-0.03m);
    }

    [Fact]
    public void LaSerieTraeTodosLosMesesDelPeriodoEnFechaDeBogota()
    {
        // 1 de abril 03:00 UTC es todavía 31 de marzo en Bogotá.
        var finDeMarzo = Fila(ganada: true, creada: new DateTimeOffset(2026, 4, 1, 3, 0, 0, TimeSpan.Zero));
        var mayo = Fila(ganada: false, creada: new DateTimeOffset(2026, 5, 20, 12, 0, 0, _bogota));

        var serie = clsCalculadoraMetricas.SerieMensual([finDeMarzo, mayo, Fila(vigente: false)], new DateOnly(2026, 3, 15), new DateOnly(2026, 5, 2), FechaBogota);

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
    public void SinColumnasDeCiudadLaConcentracionNoEstaDisponible()
    {
        var concentracion = clsCalculadoraMetricas.Concentracion([Fila(), Fila(vigente: false)], disponible: false);

        concentracion.Should().BeEquivalentTo(new clsConcentracionCiudades(false, [], [], 1, 1));
    }
}
