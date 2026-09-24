using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aplicacion.Tests;

public sealed class clsDependenciasAplicacionTests
{
    [Fact]
    public void AddAplicacionDevuelveLaMismaColeccionParaEncadenar()
    {
        var servicios = new ServiceCollection();

        var devuelta = servicios.AddAplicacion();

        devuelta.Should().BeSameAs(servicios);
    }

    [Fact]
    public void AddAplicacionConColeccionNulaLanzaArgumentNull()
    {
        IServiceCollection servicios = null!;

        var registrar = () => servicios.AddAplicacion();

        registrar.Should().Throw<ArgumentNullException>();
    }
}
