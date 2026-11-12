using System.Globalization;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Enviar solo códigos conocidos: ¿Nexo conoce el código? Con la equivalencia de Nexo y de <c>MismoCodigo</c>: sin
/// tildes ni mayúsculas y con <c>_</c>/<c>-</c> como espacio; devuelve el código escrito como en el catálogo de Nexo.
/// </summary>
public sealed class clsCodigosConocidosNexoTests
{
    private static readonly string[] _catalogo = ["2S3", "3S3", "CAMA BAJA", "Tractomula", "C-2", "C 2"];

    [Theory]
    [InlineData("3S3", "3S3")]
    [InlineData(" 3S3 ", "3S3")]
    [InlineData("3s3", "3S3")]
    [InlineData("cama_baja", "CAMA BAJA")]
    [InlineData("Cama-Baja", "CAMA BAJA")]
    [InlineData("CÁMA BAJA", "CAMA BAJA")]
    [InlineData("TRACTOMÚLA", "Tractomula")]
    [InlineData("C 2", "C 2")]
    [InlineData("C-2", "C-2")]
    public void UnCodigoExactoOEquivalenteSinAmbiguedadEsConocido(string codigo, string esperado) =>
        clsCodigosConocidosNexo.Resolver(_catalogo, codigo).Should().Be(esperado);

    [Theory]
    [InlineData("2S2")]
    [InlineData("3 S 3")]
    [InlineData("3S")]
    [InlineData("33S")]
    [InlineData("CAMABAJA")]
    [InlineData("Tractocamión 3 ejes, semi 3")]
    [InlineData("C_2")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnCodigoDesconocidoVacioOAmbiguoNoEsConocido(string? codigo) =>
        clsCodigosConocidosNexo.Resolver(_catalogo, codigo).Should().BeNull("C_2 equivale a C-2 y a C 2: ambiguo; los demás no están");

    [Fact]
    public void SinCatalogoNingunCodigoEsConocido()
    {
        clsCodigosConocidosNexo.Resolver(null, "3S3").Should().BeNull();
        clsCodigosConocidosNexo.Resolver([], "3S3").Should().BeNull();
        clsCodigosConocidosNexo.Resolver([null, " "], "3S3").Should().BeNull();
    }

    [Theory]
    [InlineData("FURGON_REFRIGERADO", "FURGON REFRIGERADO")]
    [InlineData("Furgon Refrigerado", "FURGON REFRIGERADO")]
    [InlineData("furgón", "FURGON")]
    [InlineData("estibas", "ESTIBAS")]
    [InlineData("CAMA_BAJA", null)]
    [InlineData("TOLVA", null)]
    public void LaCarroceriaSeResuelveContraLaListaDeNexo(string codigo, string? esperado) =>
        clsCodigosConocidosNexo.Carroceria(codigo).Should().Be(esperado);

    [Fact]
    public void LaComparacionNoDependeDeLaCulturaDelProceso()
    {
        // En tr-TR, "i".ToUpper() es "İ": la equivalencia usa ToUpperInvariant y no debe cambiar con la cultura.
        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            clsCodigosConocidosNexo.Carroceria("estibas").Should().Be("ESTIBAS");
            clsCodigosConocidosNexo.Resolver(["TRAILER"], "trailer").Should().Be("TRAILER");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }
}
