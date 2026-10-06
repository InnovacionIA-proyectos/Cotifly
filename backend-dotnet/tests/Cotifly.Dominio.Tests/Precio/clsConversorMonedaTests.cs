using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Dominio.Tests.Precio;

/// <summary>Monedas y conversión de pesos a dólares (<see cref="clsConversorMoneda"/>, HU-012.2).</summary>
public sealed class clsConversorMonedaTests
{
    [Theory]
    [InlineData(null, "COP")]
    [InlineData("", "COP")]
    [InlineData("  ", "COP")]
    [InlineData("cop", "COP")]
    [InlineData(" USD ", "USD")]
    [InlineData("usd", "USD")]
    public void NormalizaLasMonedasSoportadas(string? moneda, string esperada) =>
        clsConversorMoneda.Normalizar(moneda).Valor.Should().Be(esperada);

    [Theory]
    [InlineData("EUR")]
    [InlineData("US$")]
    [InlineData("pesos")]
    public void UnaMonedaNoSoportadaEsPrecioInvalido(string moneda)
    {
        var resultado = clsConversorMoneda.Normalizar(moneda);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        resultado.Error.Mensaje.Should().Contain(moneda.Trim());
    }

    [Theory]
    [InlineData(3_294_798, "3306.86", "996.35")]
    [InlineData(3_234_750, "3306.86", "978.19")]
    [InlineData(1_000_000, "4000", "250.00")]
    [InlineData(0, "4000", "0")]
    public void ConvierteAPesosPorTrmConDosDecimales(int precioPesos, string trm, string esperado) =>
        clsConversorMoneda.PesosADolares(precioPesos, Numero(trm)).Valor.Should().Be(Numero(esperado));

    [Theory]
    [InlineData("0.125", "0.12")]
    [InlineData("0.135", "0.14")]
    [InlineData("0.145", "0.14")]
    public void ElRedondeoADosDecimalesEsHalfToEven(string cociente, string esperado) =>
        // precio en pesos = cociente × 1000 con TRM 1000: el empate exacto va al par.
        clsConversorMoneda.PesosADolares(Numero(cociente) * 1000m, 1000m).Valor.Should().Be(Numero(esperado));

    [Theory]
    [InlineData("0")]
    [InlineData("-3306.86")]
    public void UnaTrmNoPositivaEsPrecioInvalido(string trm) =>
        clsConversorMoneda.PesosADolares(1_000_000m, Numero(trm)).Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);

    [Fact]
    public void UnPrecioNegativoEsPrecioInvalido() =>
        clsConversorMoneda.PesosADolares(-1m, 3306.86m).Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);

    [Theory]
    [InlineData(null, 0)]
    [InlineData("COP", 0)]
    [InlineData("USD", 2)]
    public void LosDecimalesDependenDeLaMoneda(string? moneda, int decimales) =>
        clsConversorMoneda.Decimales(moneda).Should().Be(decimales);

    [Theory]
    [InlineData("COP", "1500.5", "1500")]
    [InlineData("COP", "1501.5", "1502")]
    [InlineData("USD", "996.355", "996.36")]
    [InlineData("USD", "996.345", "996.34")]
    public void RedondeaSegunLaMoneda(string moneda, string monto, string esperado) =>
        clsConversorMoneda.Redondear(Numero(monto), moneda).Should().Be(Numero(esperado));

    private static decimal Numero(string texto) => decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);
}
