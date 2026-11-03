using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// <see cref="clsAvisosArranqueSapTm"/> (HU-042.2, #243): las variables de los modos de SAP TM que se retiraron (<c>Modo</c> y
/// <c>ExportPath</c>) no impiden arrancar; cada una deja un warning "variable obsoleta, se ignora" con su nombre y sin su valor. <c>Url</c>,
/// <c>User</c>, <c>Pass</c> y <c>SapClient</c> volvieron con la sincronización del backend: ya no avisan.
/// </summary>
public sealed class clsAvisosArranqueSapTmTests
{
    private const string ClaveDeEjemplo = "Kq7-Vr2m-Xz9p-no-debe-aparecer";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CadaVariableRetiradaConValorDejaUnWarningSinSuValor()
    {
        var configuracion = Configuracion(new()
        {
            ["SapTm:Modo"] = "odata",
            ["SapTm:Url"] = "https://sap.example/odata",
            ["SapTm:User"] = "svc_cotifly",
            ["SapTm:Pass"] = ClaveDeEjemplo,
            ["SapTm:SapClient"] = "100",
            ["SapTm:ExportPath"] = "/data/sap/sap_export.json",
            ["SapTm:SalesOrgDefault"] = "50000005",
        });

        var logger = await ArrancarAsync(configuracion);

        logger.Registros.Should().HaveCount(2).And.OnlyContain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("variable obsoleta, se ignora"));
        logger.Registros.Select(r => r.Mensaje.Split(':')[0]).Should().BeEquivalentTo("SapTm__Modo", "SapTm__ExportPath");
        logger.Registros.Should().NotContain(r => r.Mensaje.Contains(ClaveDeEjemplo) || r.Mensaje.Contains("svc_cotifly")
            || r.Mensaje.Contains("sap.example") || r.Mensaje.Contains("/data/sap"));
    }

    [Fact]
    public async Task VariablesVaciasOSoloSalesOrgDefaultNoAvisan()
    {
        var configuracion = Configuracion(new() { ["SapTm:Modo"] = "", ["SapTm:ExportPath"] = "  ", ["SapTm:SalesOrgDefault"] = "50000010" });

        (await ArrancarAsync(configuracion)).Registros.Should().BeEmpty();
    }

    [Fact]
    public void ConVariablesRetiradasLaApiArrancaYLasOpcionesSonValidas()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso(Environments.Development));
        servicios.AddLogging();
        servicios.AddInfraestructura(Configuracion(new() { ["SapTm:Modo"] = "odata", ["SapTm:ExportPath"] = "/data/sap/sap_export.json" }));
        using var proveedor = servicios.BuildServiceProvider();

        var leer = () => proveedor.GetRequiredService<IOptions<Aplicacion.Margen.Services.clsOpcionesMargen>>().Value;

        leer.Should().NotThrow("las variables de los modos retirados ya no se validan: Cotifly no las usa");
        // Solo los servicios alojados registrados con fábrica (el de los avisos lleva la lista de variables ya calculada).
        var avisos = servicios.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)
            .Select(d => d.ImplementationFactory!(proveedor)).OfType<clsAvisosArranqueSapTm>().ToList();
        avisos.Should().ContainSingle();
    }

    private static IConfiguration Configuracion(Dictionary<string, string?> valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

    private static async Task<clsLoggerFalso<clsAvisosArranqueSapTm>> ArrancarAsync(IConfiguration configuracion)
    {
        var logger = new clsLoggerFalso<clsAvisosArranqueSapTm>();
        var avisos = new clsAvisosArranqueSapTm(clsAvisosArranqueSapTm.VariablesObsoletas(configuracion), logger);

        await avisos.StartAsync(Ct);
        await avisos.StopAsync(Ct);
        return logger;
    }
}
