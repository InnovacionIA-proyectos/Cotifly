using System.Net;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Clientes;

/// <summary>
/// Selección del buscador de clientes por <c>Nexo:UrlBase</c> (<see cref="clsFabricaBuscadorClientes"/>, HU-005.2) y tubería de
/// resiliencia del cliente HTTP de Nexo.
/// </summary>
public sealed class clsRegistroBuscadorClientesTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinUrlBaseElBuscadorEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = url });

        proveedor.GetRequiredService<IClsBuscadorClientes>().Should().BeOfType<clsBuscadorClientesNoDisponible>();
    }

    [Fact]
    public void ConUrlBaseElBuscadorEsElDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });

        proveedor.GetRequiredService<IClsBuscadorClientes>().Should().BeOfType<clsBuscadorClientesNexo>();
    }

    [Fact]
    public async Task ElClienteDeNexoReintentaUnErrorTransitorio()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).ResponderCandidatos(
            """[{"nit":"900123456","nombre":"LONGYIN S.A.S.","origen":"cliente"}]""");
        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsBuscadorClientes>().BuscarAsync(_usuarioId, "longyin", Ct);

        resultado.EsExitoso.Should().BeTrue();
        nexo.Peticiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElClienteDeNexoAgotaLosReintentosYReportaBusquedaNoDisponible()
    {
        var nexo = new clsNexoClientesFalso();
        for (var i = 0; i < 5; i++)
        {
            nexo.Responder(HttpStatusCode.InternalServerError);
        }

        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsBuscadorClientes>().BuscarAsync(_usuarioId, "x", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
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
