using Cotifly.Infraestructura.Configuracion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Api.Tests.Configuracion;

/// <summary>El patrón de opciones con ValidateOnStart impide arrancar con configuración inválida.</summary>
public sealed class clsArranqueConfiguracionTests
{
    [Fact]
    public void ConZonaHorariaInvalidaLaAppNoArranca()
    {
        using var factory = CrearConZona("Marte/Olimpo");

        var arrancar = () => factory.CreateClient();

        arrancar.Should().Throw<OptionsValidationException>().WithMessage("*Zona:ZonaHoraria*");
    }

    [Fact]
    public void SinConfiguracionLaZonaEsAmericaBogota()
    {
        using var factory = new clsCotiflyApiFactory();

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesZona>>().Value;

        opciones.ZonaHoraria.Should().Be("America/Bogota");
    }

    [Fact]
    public void LaZonaSeLeeDeLaConfiguracion()
    {
        using var factory = CrearConZona("UTC");

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesZona>>().Value;

        opciones.ZonaHoraria.Should().Be("UTC");
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CrearConZona(string zona) =>
        new clsCotiflyApiFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, configuracion) =>
                configuracion.AddInMemoryCollection(new Dictionary<string, string?> { ["Zona:ZonaHoraria"] = zona })));
}
