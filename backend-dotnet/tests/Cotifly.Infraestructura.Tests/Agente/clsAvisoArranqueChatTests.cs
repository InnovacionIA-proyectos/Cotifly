using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Agente;

/// <summary><see cref="clsAvisoArranqueChat"/>: la creación por chat habilitada en Production se avisa al arrancar (HU-013.6, #102).</summary>
public sealed class clsAvisoArranqueChatTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnProduccionConLaCreacionHabilitadaAvisa() =>
        (await ArrancarAsync(true, Environments.Production)).Registros.Should().ContainSingle()
            .Which.Should().Match<clsRegistroLog>(r => r.Nivel == LogLevel.Warning && r.Mensaje == clsAvisoArranqueChat.AvisoCreacionHabilitada);

    [Theory]
    [InlineData(false, "Production")]
    [InlineData(true, "Development")]
    [InlineData(true, "Staging")]
    public async Task EnLosDemasCasosNoAvisa(bool habilitada, string entorno) =>
        (await ArrancarAsync(habilitada, entorno)).Registros.Should().BeEmpty();

    [Fact]
    public void AddInfraestructuraRegistraElAvisoComoServicioAlojado()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(new ConfigurationBuilder().Build());

        servicios.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(clsAvisoArranqueChat));
    }

    private static async Task<clsLoggerFalso<clsAvisoArranqueChat>> ArrancarAsync(bool habilitada, string entorno)
    {
        var logger = new clsLoggerFalso<clsAvisoArranqueChat>();
        var aviso = new clsAvisoArranqueChat(Options.Create(new clsOpcionesChat { CreacionHabilitada = habilitada }), new clsEntornoFalso(entorno), logger);
        await aviso.StartAsync(Ct);
        await aviso.StopAsync(Ct);
        return logger;
    }
}
