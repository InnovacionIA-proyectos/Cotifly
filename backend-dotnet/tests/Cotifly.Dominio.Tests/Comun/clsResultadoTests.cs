using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Tests.Comun;

public sealed class clsResultadoTests
{
    private static readonly clsErrorDominio _errorPrecio = new("PRECIO_INVALIDO", "El costo debe ser mayor que cero.");

    [Fact]
    public void ExitoExponeElValorYNoTieneError()
    {
        var resultado = clsResultado<decimal>.Exito(3294798m);

        resultado.EsExitoso.Should().BeTrue();
        resultado.EsFallo.Should().BeFalse();
        resultado.Valor.Should().Be(3294798m);
    }

    [Fact]
    public void FalloExponeElErrorYNoEsExitoso()
    {
        var resultado = clsResultado<decimal>.Fallo(_errorPrecio);

        resultado.EsExitoso.Should().BeFalse();
        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Should().Be(_errorPrecio);
    }

    [Fact]
    public void LeerValorDeUnFalloLanzaInvalidOperation()
    {
        var resultado = clsResultado<decimal>.Fallo(_errorPrecio);

        var leer = () => resultado.Valor;

        leer.Should().Throw<InvalidOperationException>().WithMessage("*PRECIO_INVALIDO*");
    }

    [Fact]
    public void LeerErrorDeUnExitoLanzaInvalidOperation()
    {
        var resultado = clsResultado<string>.Exito("PCN-24092026");

        var leer = () => resultado.Error;

        leer.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ExitoConValorNuloLanzaArgumentNull()
    {
        var crear = () => clsResultado<string>.Exito(null!);

        crear.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FalloConErrorNuloLanzaArgumentNull()
    {
        var crear = () => clsResultado<string>.Fallo(null!);

        crear.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(true, "exito:10")]
    [InlineData(false, "fallo:PRECIO_INVALIDO")]
    public void CoincidirEjecutaSoloLaRamaQueCorresponde(bool esExitoso, string esperado)
    {
        var resultado = esExitoso
            ? clsResultado<int>.Exito(10)
            : clsResultado<int>.Fallo(_errorPrecio);

        var texto = resultado.Coincidir(
            valor => $"exito:{valor}",
            error => $"fallo:{error.Codigo}");

        texto.Should().Be(esperado);
    }

    [Fact]
    public void ConversionImplicitaDesdeErrorCreaUnFallo()
    {
        clsResultado<int> resultado = _errorPrecio;

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("PRECIO_INVALIDO");
    }

    [Fact]
    public void ConversionImplicitaDesdeValorCreaUnExito()
    {
        clsResultado<string> resultado = "PCN-1";

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("PCN-1");
    }

    [Fact]
    public void ConversionImplicitaDesdeValorNuloLanzaArgumentNull()
    {
        string? nulo = null;

        var convertir = () => { clsResultado<string> _ = nulo!; };

        convertir.Should().Throw<ArgumentNullException>();
    }
}
