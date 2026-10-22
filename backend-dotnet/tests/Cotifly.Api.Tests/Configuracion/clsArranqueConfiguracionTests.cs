using Cotifly.Aplicacion.Costeo.Services;
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

    // --- HU-032 (#197): Captura:ClasesSinVehiculoObligatorio ---

    [Fact]
    public void SinConfigurarLasClasesSinVehiculoObligatorioSonSoloContenedor()
    {
        using var factory = new clsCotiflyApiFactory();

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesCaptura>>().Value;

        opciones.ClasesSinVehiculo.Should().Equal("Contenedor");
    }

    [Fact]
    public void LaListaConfiguradaDeClasesSinVehiculoObligatorioReemplazaLaDePorDefecto()
    {
        using var factory = Crear(new() { ["Captura:ClasesSinVehiculoObligatorio:0"] = "Contenedor", ["Captura:ClasesSinVehiculoObligatorio:1"] = " Carga Suelta " });

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesCaptura>>().Value;

        opciones.ClasesSinVehiculo.Should().Equal("Contenedor", "Carga Suelta");
    }

    [Theory]
    [InlineData("Carga suelta")]
    [InlineData("CargaSuelta")]
    [InlineData("Granel")]
    [InlineData(" ")]
    public void UnaClaseSinVehiculoObligatorioDesconocidaImpideArrancar(string clase)
    {
        using var factory = Crear(new() { ["Captura:ClasesSinVehiculoObligatorio:0"] = "Contenedor", ["Captura:ClasesSinVehiculoObligatorio:1"] = clase });

        var arrancar = () => factory.CreateClient();

        arrancar.Should().Throw<OptionsValidationException>().WithMessage("*Captura:ClasesSinVehiculoObligatorio*");
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Crear(Dictionary<string, string?> valores) =>
        new clsCotiflyApiFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, configuracion) => configuracion.AddInMemoryCollection(valores)));

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CrearConZona(string zona) =>
        new clsCotiflyApiFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, configuracion) =>
                configuracion.AddInMemoryCollection(new Dictionary<string, string?> { ["Zona:ZonaHoraria"] = zona })));
}
