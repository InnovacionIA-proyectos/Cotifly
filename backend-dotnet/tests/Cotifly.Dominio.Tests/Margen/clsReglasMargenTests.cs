using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Tests.Margen;

/// <summary>Paridad con las pruebas puras de <c>tests/test_margen.py</c> y resolución de línea sin tildes (deuda de ADR-008).</summary>
public sealed class clsReglasMargenTests
{
    // test_normalizar_value
    [Theory]
    [InlineData("13.5", "0.135")]
    [InlineData("5", "0.05")]
    [InlineData("-19.2", "-0.192")]
    [InlineData("99.9", "0.999")]
    [InlineData("12.3456789", "0.123457")] // round(x/100, 6): 0.123456789 → 0.123457
    [InlineData("0.00005", "0")]           // 0.0000005 → 0 (half-to-even a 6 decimales)
    public void NormalizaElValueDeSapAFraccionConSeisDecimales(string valor, string esperado) =>
        clsReglasMargen.NormalizarValorSap(decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture));

    // test_es_anomalo
    [Theory]
    [InlineData("0.135", false)]
    [InlineData("-0.192", true)] // negativo
    [InlineData("0.999", true)]  // > 60 %
    [InlineData("0.6", false)]   // borde inclusivo
    [InlineData("0", false)]     // borde inclusivo
    [InlineData("0.600001", true)]
    public void MarcaAnomaloFueraDeCeroASesentaPorCiento(string margen, bool anomalo) =>
        clsReglasMargen.EsAnomalo(decimal.Parse(margen, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(anomalo);

    [Fact]
    public void RedondearQuitaLosCerosALaDerecha()
    {
        var margen = clsReglasMargen.Redondear(0.1500m);

        margen.Should().Be(0.15m);
        clsReglasMargen.Texto(margen).Should().Be("0.15");
        clsReglasMargen.Texto(13.50m).Should().Be("13.5");
    }

    [Theory]
    [InlineData("Distribución", "DISTRIBUCION")]
    [InlineData("  importación  ", "IMPORTACION")]
    [InlineData("Almacenamiento   3PL", "ALMACENAMIENTO3PL")]
    [InlineData(" almacenamiento 3 p l ", "ALMACENAMIENTO3PL")]
    [InlineData("itr", "ITR")]
    [InlineData("Ñandú", "NANDU")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizarLineaQuitaTildesMayusculasYEspacios(string? linea, string esperado) =>
        clsReglasMargen.NormalizarLinea(linea).Should().Be(esperado);

    [Theory]
    [InlineData("Distribucion", "distribución", true)]
    [InlineData("Exportacion", "EXPORTACIÓN", true)]
    [InlineData("Almacenamiento3PL", "almacenamiento3pl", true)]
    [InlineData("Almacenamiento3PL", "ALMACENAMIENTO 3PL", true)]
    [InlineData("Almacenamiento", "Almacenamiento3PL", false)]
    [InlineData("Nacional", "", false)]
    public void MismaLineaComparaSinTildesNiMayusculas(string a, string b, bool iguales) =>
        clsReglasMargen.MismaLinea(a, b).Should().Be(iguales);
}
