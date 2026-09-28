using System.Net;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.CatalogosFlete.Services;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.CatalogosFlete;

/// <summary>
/// Selección del proveedor de catálogos de flete por <c>Nexo:UrlBase</c> (<see cref="clsFabricaCatalogosFlete"/>) y
/// tubería de resiliencia del cliente HTTP de Nexo (compartida con la búsqueda de clientes, mismo nombre de cliente
/// <see cref="clsBuscadorClientesNexo.NombreCliente"/>).
/// </summary>
public sealed class clsRegistroCatalogosFleteTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinUrlBaseElProveedorEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = url });

        proveedor.GetRequiredService<IClsCatalogosFleteProvider>().Should().BeOfType<clsCatalogosFleteNoDisponible>();
    }

    [Fact]
    public void ConUrlBaseElProveedorEsElDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });

        proveedor.GetRequiredService<IClsCatalogosFleteProvider>().Should().BeOfType<clsCatalogosFleteNexo>();
    }

    [Fact]
    public async Task ElClienteDeNexoReintentaUnErrorTransitorio()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).ResponderCandidatos(
            """{"tiposVehiculo":[{"valor":"NHR","etiqueta":"NHR"}],"tiposCarroseria":[],"clasesViaje":[]}""");
        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsCatalogosFleteProvider>().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        nexo.Peticiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElClienteDeNexoAgotaLosReintentosYReportaCatalogosFleteNoDisponible()
    {
        var nexo = new clsNexoClientesFalso();
        for (var i = 0; i < 5; i++)
        {
            nexo.Responder(HttpStatusCode.InternalServerError);
        }

        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsCatalogosFleteProvider>().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        nexo.Peticiones.Should().HaveCount(1 + clsBuscadorClientesNexo.Reintentos);
    }

    private static void UsarNexoFalso(IServiceCollection servicios, clsNexoClientesFalso nexo)
    {
        servicios.AddHttpClient(clsBuscadorClientesNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);

        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
        servicios.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
    }

    private static ServiceProvider Proveedor(
        Dictionary<string, string?> valores, string entorno = "Development", Action<IServiceCollection>? configurar = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso(entorno));
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }
}
