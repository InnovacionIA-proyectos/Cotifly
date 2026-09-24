using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Infraestructura.Agente.Repositories;
using Cotifly.Infraestructura.Auditoria.Repositories;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Repositories;
using Cotifly.Infraestructura.Cotizaciones.Repositories;
using Cotifly.Infraestructura.Identidad.Persistence;
using Cotifly.Infraestructura.Identidad.Repositories;
using Cotifly.Infraestructura.Margen.Repositories;
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

    [Theory]
    [InlineData(typeof(IClsUnitOfWork), typeof(clsUnitOfWork))]
    [InlineData(typeof(IClsCotizacionRepository), typeof(clsCotizacionRepository))]
    [InlineData(typeof(IClsMargenPoliticaRepository), typeof(clsMargenPoliticaRepository))]
    [InlineData(typeof(IClsSapMapeoRepository), typeof(clsSapMapeoRepository))]
    [InlineData(typeof(IClsMargenAjusteRepository), typeof(clsMargenAjusteRepository))]
    [InlineData(typeof(IClsUsuarioWebRepository), typeof(clsUsuarioWebRepository))]
    [InlineData(typeof(IClsCorreoRepository), typeof(clsCorreoRepository))]
    [InlineData(typeof(IClsAuditoriaRepository), typeof(clsAuditoriaRepository))]
    [InlineData(typeof(IClsSesionRepository), typeof(clsSesionRepository))]
    public void AddInfraestructuraRegistraLosPuertosDePersistenciaComoScoped(Type puerto, Type implementacion)
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(_configuracionVacia);

        servicios.Should().ContainSingle(d => d.ServiceType == puerto)
            .Which.Should().Match<ServiceDescriptor>(d => d.ImplementationType == implementacion && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddInfraestructuraRegistraLosContextosYLasOpcionesDeConexion()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(_configuracionVacia);

        servicios.Should().Contain(d => d.ServiceType == typeof(clsCotiflyDbContext));
        servicios.Should().Contain(d => d.ServiceType == typeof(clsIdentidadDbContext));
        servicios.Should().Contain(d => d.ServiceType == typeof(IValidateOptions<clsOpcionesConexiones>)
                                        && d.ImplementationType == typeof(clsValidadorOpcionesConexiones));
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
