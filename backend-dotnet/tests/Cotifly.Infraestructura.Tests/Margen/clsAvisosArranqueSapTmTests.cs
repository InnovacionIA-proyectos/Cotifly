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
/// <see cref="clsAvisosArranqueSapTm"/> (HU-042.2, #243): las variables del modo en vivo de SAP TM que se retiraron no impiden arrancar;
/// cada una deja un warning "variable obsoleta, se ignora" con su nombre y sin su valor.
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

        logger.Registros.Should().HaveCount(6).And.OnlyContain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("variable obsoleta, se ignora"));
        logger.Registros.Select(r => r.Mensaje.Split(':')[0]).Should().BeEquivalentTo(
            "SapTm__Modo", "SapTm__Url", "SapTm__User", "SapTm__Pass", "SapTm__SapClient", "SapTm__ExportPath");
        logger.Registros.Should().NotContain(r => r.Mensaje.Contains(ClaveDeEjemplo) || r.Mensaje.Contains("svc_cotifly")
            || r.Mensaje.Contains("sap.example"));
    }

    [Fact]
    public async Task VariablesVaciasOSoloSalesOrgDefaultNoAvisan()
    {
        var configuracion = Configuracion(new() { ["SapTm:Modo"] = "", ["SapTm:Pass"] = "  ", ["SapTm:SalesOrgDefault"] = "50000010" });

        (await ArrancarAsync(configuracion)).Registros.Should().BeEmpty();
    }

    [Fact]
    public void ConVariablesRetiradasLaApiArrancaYLasOpcionesSonValidas()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso(Environments.Development));
        servicios.AddLogging();
        servicios.AddInfraestructura(Configuracion(new() { ["SapTm:Modo"] = "odata", ["SapTm:Pass"] = "corta" }));
        using var proveedor = servicios.BuildServiceProvider();

        var leer = () => proveedor.GetRequiredService<IOptions<Aplicacion.Margen.Services.clsOpcionesMargen>>().Value;

        leer.Should().NotThrow("una clave de SAP corta o de ejemplo ya no se valida: Cotifly no la usa");
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
