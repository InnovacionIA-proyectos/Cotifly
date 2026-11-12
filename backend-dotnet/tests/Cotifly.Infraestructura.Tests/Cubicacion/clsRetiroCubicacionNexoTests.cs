using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Cubicacion;

/// <summary>
/// Ningún adaptador ni registro usa la cubicación rápida de Nexo (<c>POST /api/cubicacion</c>): "Calcular
/// cubicaje" va al cubicador por <see cref="IClsCubicador"/>, aun con <c>Nexo:UrlBase</c> configurada.
/// </summary>
public sealed class clsRetiroCubicacionNexoTests
{
    [Theory]
    [InlineData("Cotifly.Infraestructura.Cubicacion.Services.clsCubicacionNexo")]
    [InlineData("Cotifly.Infraestructura.Cubicacion.Services.clsCubicacionNoDisponible")]
    [InlineData("Cotifly.Infraestructura.Cubicacion.Services.clsFabricaCubicacion")]
    public void LosAdaptadoresDeLaCubicacionDeNexoYaNoExisten(string tipo) =>
        typeof(clsCubicadorHttp).Assembly.GetType(tipo).Should().BeNull();

    [Fact]
    public void NingunServicioRegistradoEsDeLaCubicacionDeNexo()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddInfraestructura(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nexo:UrlBase"] = "http://nexo-test.local" }).Build());

        servicios.Should().NotContain(d => d.ServiceType.Name.Contains("CubicacionNexo", StringComparison.Ordinal)
            || d.ServiceType.Name.Contains("CubicacionProvider", StringComparison.Ordinal)
            || (d.ImplementationType != null && d.ImplementationType.Name.Contains("CubicacionNexo", StringComparison.Ordinal)));
        servicios.Should().Contain(d => d.ServiceType == typeof(IClsCubicador));
    }
}
