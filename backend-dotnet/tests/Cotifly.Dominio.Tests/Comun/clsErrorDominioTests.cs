using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Tests.Comun;

public sealed class clsErrorDominioTests
{
    [Fact]
    public void ConservaCodigoMensajeYDetalle()
    {
        var error = new clsErrorDominio("MARGEN_LINEA_DESCONOCIDA", "La línea no tiene margen.", "linea=Cabotaje");

        error.Codigo.Should().Be("MARGEN_LINEA_DESCONOCIDA");
        error.Mensaje.Should().Be("La línea no tiene margen.");
        error.Detalle.Should().Be("linea=Cabotaje");
    }

    [Fact]
    public void DetalleEsOpcional()
    {
        var error = new clsErrorDominio("PRECIO_INVALIDO", "El costo debe ser mayor que cero.");

        error.Detalle.Should().BeNull();
    }

    [Fact]
    public void DosErroresConLosMismosDatosSonIguales()
    {
        var a = new clsErrorDominio("PRECIO_INVALIDO", "x");
        var b = new clsErrorDominio("PRECIO_INVALIDO", "x");

        a.Should().Be(b);
    }

    [Theory]
    [InlineData("", "mensaje")]
    [InlineData("   ", "mensaje")]
    [InlineData("CODIGO", "")]
    [InlineData("CODIGO", "  ")]
    public void CodigoOMensajeVacioLanzaArgumentException(string codigo, string mensaje)
    {
        var crear = () => new clsErrorDominio(codigo, mensaje);

        crear.Should().Throw<ArgumentException>();
    }
}
