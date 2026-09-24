using System.Globalization;
using Cotifly.Infraestructura.Configuracion;

namespace Cotifly.Infraestructura.Tests.Configuracion;

public sealed class clsValidadorSecretosTests
{
    private const string Clave = "Jwt:SigningKey";
    private const int LargoMinimo = 32;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnProduccionUnSecretoVacioFalla(string? valor)
    {
        var error = clsValidadorSecretos.Validar(Clave, valor, LargoMinimo, esProduccion: true);

        error.Should().NotBeNull().And.Contain(Clave).And.Contain("vacío");
    }

    [Theory]
    [InlineData("<secreto>")]
    [InlineData("<secreto-aleatorio-de-al-menos-32-bytes>")]
    [InlineData("cambiar-en-despliegue-por-un-valor-largo-de-verdad")]
    [InlineData("CHANGEME-changeme-changeme-changeme-changeme")]
    [InlineData("dev")]
    [InlineData("DEV")]
    public void EnProduccionUnSecretoConPlaceholderFalla(string valor)
    {
        var error = clsValidadorSecretos.Validar(Clave, valor, LargoMinimo, esProduccion: true);

        error.Should().NotBeNull().And.Contain(Clave).And.Contain("placeholder");
    }

    [Fact]
    public void EnProduccionUnSecretoMasCortoQueElMinimoFalla()
    {
        var error = clsValidadorSecretos.Validar(Clave, "k9Xq2mPz7Lw4", LargoMinimo, esProduccion: true);

        error.Should().NotBeNull().And.Contain(Clave).And.Contain(LargoMinimo.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EnProduccionUnSecretoValidoPasa()
    {
        var error = clsValidadorSecretos.Validar(Clave, "u8Rk2Vq9Lm4Zp7Xw1Nc6Hs3Tb5Jy0Fd8Ga", LargoMinimo, esProduccion: true);

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<secreto>")]
    [InlineData("dev")]
    [InlineData("corto")]
    public void FueraDeProduccionNoSeValida(string valor)
    {
        var error = clsValidadorSecretos.Validar(Clave, valor, LargoMinimo, esProduccion: false);

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("k9Xq2mPz7Lw4")]
    [InlineData("cambiar-Zt5Qw8Ep3Rn6")]
    [InlineData("<secreto-Hj4Kl7>")]
    public void ElMensajeNoIncluyeElValorDelSecreto(string valor)
    {
        var error = clsValidadorSecretos.Validar(Clave, valor, LargoMinimo, esProduccion: true);

        error.Should().NotBeNull().And.NotContain(valor);
    }

    [Fact]
    public void ClaveVaciaLanzaArgumentException()
    {
        var validar = () => clsValidadorSecretos.Validar(" ", "x", LargoMinimo, esProduccion: true);

        validar.Should().Throw<ArgumentException>();
    }
}
