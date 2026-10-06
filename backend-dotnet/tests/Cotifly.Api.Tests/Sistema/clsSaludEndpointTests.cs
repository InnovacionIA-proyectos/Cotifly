using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Api.Tests.Sistema;

public sealed class clsSaludEndpointTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    [Fact]
    public async Task GetHealthDevuelve200ConLaFormaDelContrato()
    {
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var raiz = json.RootElement;
        raiz.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "servicio", "entorno");
        raiz.GetProperty("status").GetString().Should().Be("ok");
        raiz.GetProperty("servicio").GetString().Should().Be("cotifly-api");
        raiz.GetProperty("entorno").GetString().Should().Be(clsCotiflyApiFactory.EntornoPruebas);
    }

    [Fact]
    public async Task GetHealthNoExigeAutenticacion()
    {
        using var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = null;

        using var respuesta = await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void ElComposicionRootResuelveLosPuertosConSuImplementacion()
    {
        var reloj = factory.Services.GetRequiredService<IClsReloj>();

        reloj.Should().BeOfType<clsRelojSistema>();
    }
}
