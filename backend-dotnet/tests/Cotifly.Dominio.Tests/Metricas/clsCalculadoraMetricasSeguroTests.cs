using Cotifly.Dominio.Metricas;

namespace Cotifly.Dominio.Tests.Metricas;

/// <summary>
/// HU-045.2 (#264, hallazgo M1 de la auditoría): el seguro de la mercancía está en el precio de la ruta (<c>precioFinalLinea</c>) pero no
/// en su costo (<c>costoFinal</c>). Es un traslado (la prima de la póliza), no ganancia: <c>margen_ponderado</c> y <c>ganancias</c> lo
/// restan del precio en COP. El valor cotizado y el valor ganado sí lo incluyen (se le cobra al cliente).
/// </summary>
public sealed class clsCalculadoraMetricasSeguroTests
{
    private static readonly DateTimeOffset _marzo = new(2026, 3, 10, 12, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset _ahora = new(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(-5));

    /// <summary>Ejemplo de la HU: costo 1.000.000, transporte 1.250.000 (20 %) y seguro 690.000: precio de la ruta 1.940.000.</summary>
    private static clsCotizacionMetrica ConSeguro(bool? ganada = true, string moneda = "COP", decimal trm = 0m)
    {
        var precio = moneda == "COP" ? 1_940_000m : 485.00m;
        var ruta = new clsRutaMetrica
        {
            Orden = 1, LineaNegocioId = 1, LineaNegocio = "Nacional", Costo = 1_000_000m, PrecioFinal = precio, MargenAplicado = 0.20m,
            Seguro = 690_000m,
        };
        return new clsCotizacionMetrica
        {
            UsuarioId = Guid.NewGuid(),
            CreatedAt = _marzo,
            CreatedAtVigente = _marzo,
            Ganada = ganada,
            Moneda = moneda,
            TrmAplicada = trm,
            Costo = 1_000_000m,
            PrecioFinal = precio,
            Seguro = 690_000m,
            Rutas = [ruta],
        };
    }

    [Fact]
    public void ElMargenPonderadoNoCuentaElSeguroComoGanancia()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([ConSeguro()], _ahora, deadlineDisponible: false);

        indicadores.MargenPonderado.Should().Be(0.20m, "(1.250.000 − 1.000.000) / 1.250.000; con el seguro daría 48 %");
    }

    [Fact]
    public void LasGananciasRestanElSeguroYElValorCotizadoLoIncluye()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([ConSeguro()], _ahora, deadlineDisponible: false);

        indicadores.Ganancias.Should().Be(250_000m, "1.940.000 − 1.000.000 − 690.000 de seguro");
        indicadores.ValorCotizadoCop.Should().Be(1_940_000m, "el seguro se le cobra al cliente");
        indicadores.ValorGanadoCop.Should().Be(1_940_000m);
    }

    [Fact]
    public void LaAperturaPorLineaTambienRestaElSeguro()
    {
        var linea = clsCalculadoraMetricas.PorLineaNegocio([ConSeguro()]).Single();

        linea.MargenPonderado.Should().Be(0.20m);
        linea.Ganancias.Should().Be(250_000m);
        linea.ValorCotizadoCop.Should().Be(1_940_000m);
    }

    [Fact]
    public void EnDolaresElPrecioSePasaAPesosYSeRestaElSeguroQueYaEstaEnPesos()
    {
        var indicadores = clsCalculadoraMetricas.Indicadores([ConSeguro(moneda: "USD", trm: 4_000m)], _ahora, deadlineDisponible: false);

        indicadores.MargenPonderado.Should().Be(0.20m, "485 × 4.000 = 1.940.000 − 690.000 = 1.250.000");
        indicadores.Ganancias.Should().Be(250_000m);
    }

    [Fact]
    public void LaSerieMensualRestaElSeguroDeLasGanancias()
    {
        var serie = clsCalculadoraMetricas.SerieMensual([ConSeguro()], new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), f => DateOnly.FromDateTime(f.DateTime));

        serie.Single().Ganancias.Should().Be(250_000m);
    }
}
