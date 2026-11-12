using System.Globalization;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// La única fuente de equivalencia entre el código de vehículo de SAP («2», «3») y el código NEXO de SICE-TAC
/// (<c>C2</c>, <c>C3</c>), <c>tbl_mapeo_vehiculo_tarifario</c>.
/// </summary>
public sealed class clsEquivalenciaVehiculoNexoTests
{
    [Theory]
    [InlineData("2", "C2")]
    [InlineData("3", "C3")]
    [InlineData(" 2 ", "C2")]
    [InlineData("C2", "C2")]
    [InlineData("c2", "C2")]
    [InlineData("C3", "C3")]
    [InlineData("c3", "C3")]
    public void TraduceElCodigoSapOElCodigoNexoAlCodigoNexoCanonico(string codigo, string esperado) =>
        clsEquivalenciaVehiculoNexo.ACodigoNexo(codigo).Should().Be(esperado);

    [Theory]
    [InlineData("2L2")]
    [InlineData("2S3")]
    [InlineData("3S3")]
    [InlineData("2L3")]
    [InlineData("22")]
    [InlineData("C 2")]       // con separador «C 2» es otro código (dos tokens), no equivale a C2
    [InlineData("C-3")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnCodigoFueraDelMapeoNoCambia(string? codigo) =>
        clsEquivalenciaVehiculoNexo.ACodigoNexo(codigo).Should().BeNull("solo «2»/«3» difieren entre SAP y NEXO");

    [Fact]
    public void ElMapeoEsLaUnicaFuenteYCubreSoloDosPares()
    {
        clsEquivalenciaVehiculoNexo.Mapeo.Should().Equal(("2", "C2"), ("3", "C3"));
    }

    [Fact]
    public void LaEquivalenciaNoDependeDeLaCulturaDelProceso()
    {
        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            clsEquivalenciaVehiculoNexo.ACodigoNexo("c2").Should().Be("C2");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }
}
