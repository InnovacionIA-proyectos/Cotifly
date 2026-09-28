using System.Net;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Cubicacion;

/// <summary>
/// Selección del proveedor de cubicación por <c>Nexo:UrlBase</c> (<see cref="clsFabricaCubicacion"/>) y tubería de
/// resiliencia del cliente HTTP de Nexo (compartida con la búsqueda de clientes, mismo nombre de cliente
/// <see cref="clsBuscadorClientesNexo.NombreCliente"/>).
/// </summary>
public sealed class clsRegistroCubicacionTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<clsItemCubicacion> _items = [new clsItemCubicacion("Caja", 1, 100, 80, 60, 50)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinUrlBaseElProveedorEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = url });

        proveedor.GetRequiredService<IClsCubicacionProvider>().Should().BeOfType<clsCubicacionNoDisponible>();
    }

    [Fact]
    public void ConUrlBaseElProveedorEsElDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });

        proveedor.GetRequiredService<IClsCubicacionProvider>().Should().BeOfType<clsCubicacionNexo>();
    }

    [Fact]
    public async Task ElClienteDeNexoReintentaUnErrorTransitorio()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).ResponderCandidatos(
            """{"matrices":[],"sugerencia":null,"advertencias":[],"sugerenciaFlota":null,"extradimension":null}""");
        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsCubicacionProvider>().CubicarAsync(_usuarioId, _items, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        nexo.Peticiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElClienteDeNexoAgotaLosReintentosYReportaCubicacionNoDisponible()
    {
        var nexo = new clsNexoClientesFalso();
        for (var i = 0; i < 5; i++)
        {
            nexo.Responder(HttpStatusCode.InternalServerError);
        }

        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsCubicacionProvider>().CubicarAsync(_usuarioId, _items, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
        nexo.Peticiones.Should().HaveCount(1 + clsBuscadorClientesNexo.Reintentos);
    }

    [Fact]
    public async Task Un400NoReintentaYReportaCubicacionDemasiadoGrande()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.BadRequest, """{"code":"CUBICACION_INVALIDA","message":"demasiados bultos"}""");
        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsCubicacionProvider>().CubicarAsync(_usuarioId, _items, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionDemasiadoGrande);
        nexo.Peticiones.Should().ContainSingle("un 400 es culpa del request, no un fallo transitorio a reintentar");
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
