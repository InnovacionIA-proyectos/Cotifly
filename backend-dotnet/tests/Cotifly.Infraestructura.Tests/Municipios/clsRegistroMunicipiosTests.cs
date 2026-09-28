using System.Net;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Infraestructura.Municipios.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Municipios;

/// <summary>
/// Selección del proveedor de municipios por <c>Nexo:UrlBase</c> (<see cref="clsFabricaMunicipios"/>) y tubería de
/// resiliencia del cliente HTTP de Nexo (compartida con la búsqueda de clientes, mismo nombre de cliente
/// <see cref="clsBuscadorClientesNexo.NombreCliente"/>).
/// </summary>
public sealed class clsRegistroMunicipiosTests
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

        proveedor.GetRequiredService<IClsMunicipiosProvider>().Should().BeOfType<clsMunicipiosNoDisponible>();
    }

    [Fact]
    public void ConUrlBaseElProveedorEsElDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });

        proveedor.GetRequiredService<IClsMunicipiosProvider>().Should().BeOfType<clsMunicipiosNexo>();
    }

    [Fact]
    public async Task ElClienteDeNexoReintentaUnErrorTransitorio()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).ResponderCandidatos(
            """[{"id":1,"municipio":"Bogotá","departamento":"Cundinamarca","pais":"Colombia"}]""");
        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsMunicipiosProvider>().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        nexo.Peticiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElClienteDeNexoAgotaLosReintentosYReportaMunicipiosNoDisponible()
    {
        var nexo = new clsNexoClientesFalso();
        for (var i = 0; i < 5; i++)
        {
            nexo.Responder(HttpStatusCode.InternalServerError);
        }

        using var proveedor = Proveedor(
            new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase },
            configurar: s => UsarNexoFalso(s, nexo));

        var resultado = await proveedor.GetRequiredService<IClsMunicipiosProvider>().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
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
