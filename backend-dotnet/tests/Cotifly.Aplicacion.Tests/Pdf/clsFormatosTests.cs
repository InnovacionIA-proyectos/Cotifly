using System.Globalization;
using Cotifly.Aplicacion.Pdf.Services;

namespace Cotifly.Aplicacion.Tests.Pdf;

/// <summary>
/// Formato de dinero (<c>money()</c> del Python) y fecha larga (<c>fecha_larga</c>): valores exactos e independencia de la
/// cultura del proceso y de ICU (README §3: <c>ToString("C0", es-CO)</c> con ICU da <c>$ 3.294.798</c>, con espacio).
/// </summary>
public sealed class clsFormatosTests
{
    public static TheoryData<string> Culturas => new("es-CO", "en-US", "de-DE", "fr-FR", "ja-JP", "");

    [Theory]
    [InlineData(3294798, null, "$3.294.798")]
    [InlineData(3000000, "COP", "$3.000.000")]
    [InlineData(1200, "USD", "US$1.200")]
    [InlineData(1200, "usd", "US$1.200")]
    [InlineData(0, null, "$0")]
    [InlineData(999, null, "$999")]
    [InlineData(1000, null, "$1.000")]
    [InlineData(-1000, null, "$-1.000")]
    [InlineData(123456789012, null, "$123.456.789.012")]
    public void FormateaElDineroComoElPython(long valor, string? moneda, string esperado) =>
        clsFormatoMoneda.Formatear(valor, moneda).Should().Be(esperado);

    [Theory]
    [InlineData("2.5", "$2")]
    [InlineData("3.5", "$4")]
    [InlineData("3294797.5", "$3.294.798")]
    [InlineData("3294798.49", "$3.294.798")]
    public void RedondeaAPesosHalfToEven(string valor, string esperado) =>
        clsFormatoMoneda.Formatear(decimal.Parse(valor, CultureInfo.InvariantCulture)).Should().Be(esperado);

    [Fact]
    public void SinValorDevuelveLaRaya() => clsFormatoMoneda.Formatear(null).Should().Be("—");

    [Theory]
    [MemberData(nameof(Culturas))]
    public void ElDineroNoDependeDeLaCulturaDelProceso(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = cultura.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultura);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            clsFormatoMoneda.Formatear(3294798m).Should().Be("$3.294.798");
            clsFormatoMoneda.Formatear(1200m, "USD").Should().Be("US$1.200");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void LaFechaLargaUsaElMesEnEspanolYLaCiudad() =>
        clsFormatoFecha.FechaLarga(new DateOnly(2026, 9, 24), "Yumbo").Should().Be("Yumbo, 24 de septiembre de 2026");

    [Theory]
    [InlineData("2026-03-19", "Yumbo, 19 de marzo de 2026")]
    [InlineData("2026-3-9", "Yumbo, 9 de marzo de 2026")]
    [InlineData("2026-01-01", "Yumbo, 1 de enero de 2026")]
    [InlineData("2026-12-31", "Yumbo, 31 de diciembre de 2026")]
    [InlineData("19/03/2026", "Yumbo, 19/03/2026")]
    [InlineData("2026-13-01", "Yumbo, 2026-13-01")]
    public void LaFechaDelPayloadSeFormateaOSeMuestraTalCual(string iso, string esperado) =>
        clsFormatoFecha.FechaLarga(iso, "Yumbo", new DateOnly(2000, 1, 1)).Should().Be(esperado);

    [Fact]
    public void SinFechaSeUsaHoy() =>
        clsFormatoFecha.FechaLarga(null, "Cali", new DateOnly(2026, 9, 24)).Should().Be("Cali, 24 de septiembre de 2026");

    [Theory]
    [MemberData(nameof(Culturas))]
    public void LaFechaNoDependeDeLaCulturaDelProceso(string cultura)
    {
        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = cultura.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(cultura);
            clsFormatoFecha.FechaLarga("2026-09-24", "Yumbo", default).Should().Be("Yumbo, 24 de septiembre de 2026");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script>alert(1)</script>")]
    [InlineData("a\u0000b\u0007c", "abc")]
    [InlineData("línea 1\r\nlínea 2", "línea 1\nlínea 2")]
    [InlineData("a\tb", "a b")]
    [InlineData("invisible​zero‮rtl", "invisiblezerortl")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void LaLimpiezaQuitaControlesYConservaElTexto(string? entrada, string? esperado) =>
        clsTextoSeguro.Limpiar(entrada).Should().Be(esperado);
}
