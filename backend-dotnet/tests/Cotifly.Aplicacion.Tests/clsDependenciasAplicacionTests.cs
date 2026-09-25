using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Precio.Services;
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

    [Theory]
    [InlineData(typeof(IClsPrecioService), typeof(clsPrecioService), ServiceLifetime.Singleton)]
    [InlineData(typeof(IClsMargenResolverService), typeof(clsMargenResolverService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsAjusteMargenService), typeof(clsAjusteMargenService), ServiceLifetime.Scoped)]
    public void AddAplicacionRegistraLosCasosDeUsoDePrecioYMargen(Type servicio, Type implementacion, ServiceLifetime ciclo)
    {
        var servicios = new ServiceCollection();

        servicios.AddAplicacion();

        servicios.Should().ContainSingle(d => d.ServiceType == servicio)
            .Which.Should().Match<ServiceDescriptor>(d => d.ImplementationType == implementacion && d.Lifetime == ciclo);
    }
}
