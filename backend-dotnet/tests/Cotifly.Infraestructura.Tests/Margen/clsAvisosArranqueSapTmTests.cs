using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary><see cref="clsAvisosArranqueSapTm"/>: avisos de arranque que no impiden arrancar (#13).</summary>
public sealed class clsAvisosArranqueSapTmTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnProduccionSinModoAvisaQueTodoSaleDeLaPolitica()
    {
        var logger = await ArrancarAsync(new clsOpcionesSapTm(), Environments.Production);

        logger.Registros.Should().ContainSingle()
            .Which.Should().Match<clsRegistroLog>(r => r.Nivel == LogLevel.Warning && r.Mensaje == clsAvisosArranqueSapTm.AvisoNoConfigurado);
    }

    [Fact]
    public async Task FueraDeProduccionSinModoNoAvisa() =>
        (await ArrancarAsync(new clsOpcionesSapTm { Modo = " " }, Environments.Development)).Registros.Should().BeEmpty();

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task ConExportJsonInexistenteAvisaSinFallar(string entorno)
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        var logger = await ArrancarAsync(new clsOpcionesSapTm { Modo = "export_json", ExportPath = ruta }, entorno);

        logger.Registros.Should().ContainSingle()
            .Which.Should().Match<clsRegistroLog>(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains(ruta) && r.Mensaje.Contains("política"));
    }

    [Fact]
    public async Task ConExportJsonExistenteOODataNoAvisa()
    {
        (await ArrancarAsync(new clsOpcionesSapTm { Modo = "export_json", ExportPath = clsSapTmFalso.RutaMuestra }, Environments.Production))
            .Registros.Should().BeEmpty();
        (await ArrancarAsync(new clsOpcionesSapTm { Modo = "odata", Url = clsSapTmFalso.UrlBase }, Environments.Production))
            .Registros.Should().BeEmpty();
    }

    [Fact]
    public void AddInfraestructuraRegistraLosAvisosComoServicioAlojado()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(new ConfigurationBuilder().Build());

        servicios.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(clsAvisosArranqueSapTm));
    }

    private static async Task<clsLoggerFalso<clsAvisosArranqueSapTm>> ArrancarAsync(clsOpcionesSapTm opciones, string entorno)
    {
        var logger = new clsLoggerFalso<clsAvisosArranqueSapTm>();
        var avisos = new clsAvisosArranqueSapTm(Options.Create(opciones), new clsEntornoFalso(entorno), logger);

        await avisos.StartAsync(Ct);
        await avisos.StopAsync(Ct);
        return logger;
    }
}
