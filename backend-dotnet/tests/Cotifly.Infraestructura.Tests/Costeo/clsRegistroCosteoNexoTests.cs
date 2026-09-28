using System.Net;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// Selección del adaptador del costeo por <c>Nexo:UrlBase</c> (<see cref="clsFabricaCosteoNexo"/>), opciones <c>CosteoNexo</c> y
/// tubería de resiliencia del cliente <see cref="clsCosteoNexo.NombreCliente"/> (ADR-015): un POST nunca se reintenta.
/// </summary>
public sealed class clsRegistroCosteoNexoTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinUrlBaseElCosteoEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = url });
        using var ambito = proveedor.CreateScope();

        var costeo = ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>();

        costeo.Should().BeOfType<clsCosteoNexoNoDisponible>();
        costeo.EstaConfigurado.Should().BeFalse();
    }

    [Fact]
    public async Task ElNoDisponibleRespondeCosteoNexoNoDisponible()
    {
        var resultado = await new clsCosteoNexoNoDisponible().EnviarSolicitudAsync(_usuarioId, new clsSolicitudNexo("1", clsSolicitudCosteoMuestra.Crear(), null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
    }

    [Fact]
    public void ConUrlBaseElCosteoEsElDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>().Should().BeOfType<clsCosteoNexo>();
    }

    [Fact]
    public async Task UnPostQueRecibe503NoSeReintentaParaNoDuplicarLaSolicitudEnNexo()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.Created, """{"id":"x"}""");
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, s => UsarNexoFalso(s, nexo));
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>()
            .EnviarSolicitudAsync(_usuarioId, new clsSolicitudNexo("15927", clsSolicitudCosteoMuestra.Crear(), null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        nexo.Peticiones.Should().ContainSingle("el alta no es idempotente");
    }

    [Fact]
    public void LaTuberiaDelCosteoEsDe30SegundosYNoLaDeLasBusquedas()
    {
        var opciones = new Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions();

        clsCosteoNexo.ConfigurarResiliencia(opciones);

        opciones.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        opciones.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        opciones.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(opciones.AttemptTimeout.Timeout * 2);
        clsCosteoNexo.NombreCliente.Should().NotBe(Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo.NombreCliente);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    public void UnIntervaloInicialFueraDeRangoImpideArrancar(string valor)
    {
        using var proveedor = Proveedor(new() { ["CosteoNexo:IntervaloInicialSegundos"] = valor });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCosteoNexo>>().Value;

        leer.Should().Throw<OptionsValidationException>();
    }

    private static void UsarNexoFalso(IServiceCollection servicios, clsNexoClientesFalso nexo)
    {
        servicios.AddHttpClient(clsCosteoNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);

        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
        servicios.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, Action<IServiceCollection>? configurar = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }
}
