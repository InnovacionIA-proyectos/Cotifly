using Cotifly.Aplicacion.Correo.Services;
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
    [InlineData("CargaSuelta")]
    [InlineData("Granel")]
    [InlineData("Carga  Suelta")]
    public void UnaClaseSinVehiculoObligatorioDesconocidaImpideArrancar(string clase)
    {
        using var factory = Crear(new() { ["Captura:ClasesSinVehiculoObligatorio:0"] = "Contenedor", ["Captura:ClasesSinVehiculoObligatorio:1"] = clase });

        var arrancar = () => factory.CreateClient();

        arrancar.Should().Throw<OptionsValidationException>().WithMessage("*Captura:ClasesSinVehiculoObligatorio*");
    }

    [Fact]
    public void LasClasesSinVehiculoObligatorioSeAceptanSinDistinguirMayusculasYSeNormalizanALaEscrituraDeNexo()
    {
        // Ajuste de la auditoría: "Carga suelta" ya no impide arrancar; se guarda como el catálogo ("Carga Suelta"), que es con lo que se
        // compara (exacto) la clase de viaje de la ruta.
        using var factory = Crear(new()
        {
            ["Captura:ClasesSinVehiculoObligatorio:0"] = "contenedor",
            ["Captura:ClasesSinVehiculoObligatorio:1"] = " Carga suelta ",
            ["Captura:ClasesSinVehiculoObligatorio:2"] = "CARGA SUELTA",
        });

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesCaptura>>().Value;

        opciones.ClasesSinVehiculo.Should().Equal("Contenedor", "Carga Suelta");
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public void UnValorVacioEntreLasClasesSinVehiculoObligatorioSeIgnoraYLaAppArranca(string vacio)
    {
        // B1: el validador ignora los vacíos, igual que el getter (antes "__1=" impedía arrancar).
        using var factory = Crear(new() { ["Captura:ClasesSinVehiculoObligatorio:0"] = "Contenedor", ["Captura:ClasesSinVehiculoObligatorio:1"] = vacio });

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesCaptura>>().Value;

        opciones.ClasesSinVehiculo.Should().Equal("Contenedor");
    }

    [Fact]
    public void ConTodasLasClasesSinVehiculoObligatorioVaciasRigeLaListaPorDefecto()
    {
        using var factory = Crear(new() { ["Captura:ClasesSinVehiculoObligatorio:0"] = " ", ["Captura:ClasesSinVehiculoObligatorio:1"] = "" });

        var opciones = factory.Services.GetRequiredService<IOptions<clsOpcionesCaptura>>().Value;

        opciones.ClasesSinVehiculo.Should().Equal("Contenedor");
    }

    // --- #228 (decisión del 2026-10-27): Correo:MaximoDestinatarios ---

    [Fact]
    public void SinConfigurarElTopeDeDestinatariosEs10()
    {
        using var factory = new clsCotiflyApiFactory();

        factory.Services.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.MaximoDestinatarios.Should().Be(10);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("25", 25)]
    [InlineData("100", 100)]
    public void ElTopeDeDestinatariosSeLeeDeLaConfiguracion(string valor, int esperado)
    {
        using var factory = Crear(new() { ["Correo:MaximoDestinatarios"] = valor });

        factory.Services.GetRequiredService<IOptions<clsOpcionesCorreo>>().Value.MaximoDestinatarios.Should().Be(esperado);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("101")]
    [InlineData("100000")]
    public void UnTopeDeDestinatariosFueraDe1A100ImpideArrancar(string valor)
    {
        using var factory = Crear(new() { ["Correo:MaximoDestinatarios"] = valor });

        var arrancar = () => factory.CreateClient();

        arrancar.Should().Throw<OptionsValidationException>().WithMessage("*Correo:MaximoDestinatarios*");
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Crear(Dictionary<string, string?> valores) =>
        new clsCotiflyApiFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, configuracion) => configuracion.AddInMemoryCollection(valores)));

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CrearConZona(string zona) =>
        new clsCotiflyApiFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, configuracion) =>
                configuracion.AddInMemoryCollection(new Dictionary<string, string?> { ["Zona:ZonaHoraria"] = zona })));
}
