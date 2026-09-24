using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests;

public sealed class clsDependenciasInfraestructuraTests
{
    private static readonly IConfiguration _configuracionVacia = new ConfigurationBuilder().Build();

    [Fact]
    public void AddInfraestructuraRegistraElRelojComoSingleton()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(_configuracionVacia);

        servicios.Should().ContainSingle(d => d.ServiceType == typeof(IClsReloj))
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.ImplementationType == typeof(clsRelojSistema) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfraestructuraRegistraTimeProviderDelSistemaSinReemplazarUnoExistente()
    {
        var servicios = new ServiceCollection();
        var propio = TimeProvider.System;
        servicios.AddSingleton(propio);

        servicios.AddInfraestructura(_configuracionVacia);

        servicios.Where(d => d.ServiceType == typeof(TimeProvider)).Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeSameAs(propio);
    }

    [Fact]
    public void AddInfraestructuraRegistraLasValidacionesDeLaOpcionZona()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(_configuracionVacia);

        var validadores = servicios.Where(d => d.ServiceType == typeof(IValidateOptions<clsOpcionesZona>)).ToList();
        validadores.Should().Contain(d => d.ImplementationType == typeof(clsValidadorOpcionesZona));
        validadores.Should().Contain(d => d.ImplementationFactory != null, "el validador de secretos se registra para toda opción");
    }

    [Fact]
    public void AddInfraestructuraDevuelveLaMismaColeccionParaEncadenar()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(_configuracionVacia).Should().BeSameAs(servicios);
    }

    [Fact]
    public void AddInfraestructuraSinConfiguracionLanzaArgumentNull()
    {
        var registrar = () => new ServiceCollection().AddInfraestructura(null!);

        registrar.Should().Throw<ArgumentNullException>();
    }
}
