using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Dominio.Tests.Precio;

/// <summary>
/// Paridad con <c>tests/test_precio.py</c> (Python). Cada prueba indica en su comentario el test de origen;
/// la tabla completa está en docs/08-testing.md, §8.8.
/// </summary>
public sealed class clsCalculadoraPrecioTests
{
    // test_valores_exactos_arquitectura
    [Fact]
    public void ValoresExactosDeLaArquitectura()
    {
        var r = clsCalculadoraPrecio.Calcular(2_850_000m, 0.135m, "margen").Valor;

        r.Precio.Should().Be(3_294_798m);
        r.Utilidad.Should().Be(444_798m);
        r.Costo.Should().Be(2_850_000m);
        r.Margen.Should().Be(0.135m);
        r.Metodo.Should().Be("margen");
    }

    // test_markup_vs_margen
    [Fact]
    public void MarkupRindeMenosPrecioQueMargen()
    {
        var margen = clsCalculadoraPrecio.Calcular(2_850_000m, 0.135m, "margen").Valor;
        var markup = clsCalculadoraPrecio.Calcular(2_850_000m, 0.135m, "markup").Valor;

        margen.Precio.Should().Be(3_294_798m);
        markup.Precio.Should().Be(3_234_750m);
        markup.Precio.Should().BeLessThan(margen.Precio);
    }

    // test_borde_margen_cero
    [Fact]
    public void ConMargenCeroElPrecioEsElCosto()
    {
        var r = clsCalculadoraPrecio.Calcular(1_000_000m, 0m, "margen").Valor;

        r.Precio.Should().Be(1_000_000m);
        r.Utilidad.Should().Be(0m);
    }

    // test_borde_margen_uno_division_por_cero
    [Fact]
    public void ConMetodoMargenUnMargenDeUnoSeRechaza() =>
        Rechazo(1_000_000m, 1.0m, "margen").Mensaje.Should().Contain("< 1");

    // test_markup_admite_margen_alto
    [Fact]
    public void MarkupAdmiteUnMargenAlto() =>
        clsCalculadoraPrecio.Calcular(1_000_000m, 1.5m, "markup").Valor.Precio.Should().Be(2_500_000m);

    // test_rechaza_margen_negativo
    [Theory]
    [InlineData("margen")]
    [InlineData("markup")]
    public void RechazaMargenNegativo(string metodo) =>
        Rechazo(1_000_000m, -0.1m, metodo).Mensaje.Should().Contain("negativo");

    // test_rechaza_costo_no_positivo
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2_850_000)]
    public void RechazaCostoNoPositivo(int costo) =>
        Rechazo(costo, 0.1m, "margen").Mensaje.Should().Contain("positivo");

    // test_rechaza_metodo_desconocido
    [Theory]
    [InlineData("promedio")]
    [InlineData("margin")]
    public void RechazaMetodoDesconocido(string metodo) =>
        Rechazo(1_000_000m, 0.1m, metodo).Mensaje.Should().Contain("Método");

    // test_property_precio_no_menor_que_costo (hypothesis): versión determinista sobre una grilla
    // de costos en [1, 10^9] y márgenes en [0, 0.5999], con ambos métodos.
    public static TheoryData<decimal, decimal, string> Grilla()
    {
        decimal[] costos = [1m, 7m, 999m, 1_000_001m, 2_850_000m, 123_456_789m, 1_000_000_000m];
        decimal[] margenes = [0m, 0.0001m, 0.05m, 0.135m, 0.15m, 1m / 3m, 0.5m, 0.5999m];
        var datos = new TheoryData<decimal, decimal, string>();
        foreach (var costo in costos)
        {
            foreach (var margen in margenes)
            {
                datos.Add(costo, margen, "margen");
                datos.Add(costo, margen, "markup");
            }
        }

        return datos;
    }

    [Theory]
    [MemberData(nameof(Grilla))]
    public void ElPrecioNuncaEsMenorQueElCostoYLaUtilidadEsLaDiferencia(decimal costo, decimal margen, string metodo)
    {
        var r = clsCalculadoraPrecio.Calcular(costo, margen, metodo).Valor;

        r.Precio.Should().BeGreaterThanOrEqualTo(costo);
        r.Utilidad.Should().BeApproximately(r.Precio - costo, 0.01m);
        (r.Precio % 1m).Should().Be(0m, "el precio se redondea a pesos enteros");
    }

    // ADR-010 §6: redondeo half-to-even (Python round) en los puntos medios exactos.
    [Theory]
    [InlineData(1, 0.5, "markup", 2)]        // 1.5 → 2
    [InlineData(3, 0.5, "markup", 4)]        // 4.5 → 4 (par)
    [InlineData(5, 0.5, "markup", 8)]        // 7.5 → 8
    [InlineData(1, 1.5, "markup", 2)]        // 2.5 → 2 (par)
    [InlineData(1, 0.6, "margen", 2)]        // 1 / 0.4 = 2.5 → 2 (par)
    [InlineData(1.5, 0.4, "margen", 2)]      // 1.5 / 0.6 = 2.5 → 2 (par)
    [InlineData(2.1, 0.3, "margen", 3)]      // 2.1 / 0.7 = 3 (exacto)
    // Desviación documentada (ADR-010 §6): empate exacto en decimal; Python arrastra el error de float.
    [InlineData(55, 0.1, "markup", 60)]      // 60.5 → 60 (Python: 55 * 1.1 = 60.50000000000001 → 61)
    [InlineData(6349, 0.44, "margen", 11338)] // 11337.5 → 11338 (Python: 11337.499999999998 → 11337)
    public void ElPrecioSeRedondeaAPesosConHalfToEven(double costo, double margen, string metodo, int esperado) =>
        clsCalculadoraPrecio.Calcular((decimal)costo, (decimal)margen, metodo).Valor.Precio.Should().Be(esperado);

    [Fact]
    public void LaUtilidadSeRedondeaADosDecimalesConHalfToEven()
    {
        // precio = round(1000.125) = 1000; utilidad = round(1000 - 1000.125, 2) = round(-0.125, 2) = -0.12 (Python: -0.12).
        var r = clsCalculadoraPrecio.Calcular(1000.125m, 0m, "margen").Valor;

        r.Precio.Should().Be(1000m);
        r.Utilidad.Should().Be(-0.12m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" MARKUP ")]
    public void ElMetodoSeNormalizaYPorDefectoEsMargen(string? metodo)
    {
        var r = clsCalculadoraPrecio.Calcular(1_000_000m, 0.2m, metodo).Valor;

        r.Metodo.Should().Be(metodo is null or "" ? "margen" : "markup");
        r.Precio.Should().Be(metodo is null or "" ? 1_250_000m : 1_200_000m);
    }

    [Fact]
    public void UnPrecioFueraDelRangoDecimalSeRechazaSinExcepcion() =>
        Rechazo(decimal.MaxValue / 2m, 0.9999m, "margen").Mensaje.Should().Contain("rango");

    private static clsErrorDominio Rechazo(decimal costo, decimal margen, string metodo)
    {
        var resultado = clsCalculadoraPrecio.Calcular(costo, margen, metodo);
        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        return resultado.Error;
    }
}
