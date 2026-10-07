using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// Adaptadores por defecto de #10: SAP TM sin <c>SapTm:Modo</c> (los adaptadores reales de #13 se prueban en
/// <c>clsRegistroFuenteMargenTests</c>). El proveedor de tarifas por defecto de #10 se retiró con el agente legado (HU-013.9). También el registro de la opción <c>SapTm:SalesOrgDefault</c>.
/// </summary>
public sealed class clsFuentesNoDisponiblesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LaFuenteDeMargenNoConfiguradaNoTieneRegistrosYNoLanza()
    {
        var fuente = new clsFuenteMargenNoConfigurada();

        var valor = await fuente.ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        fuente.Modo.Should().Be("no_configurada");
        fuente.EstaConfigurada.Should().BeFalse();
        valor.Should().BeNull();
    }

    [Fact]
    public void SinSapTmModoLaFuenteDeMargenSigueSiendoLaNoConfigurada()
    {
        // #13: la fuente se elige por SapTm:Modo (fábrica); sin modo se conserva el comportamiento de #10.
        using var proveedor = Proveedor(salesOrgDefault: null);

        proveedor.GetRequiredService<IClsFuenteMargen>().Should().BeOfType<clsFuenteMargenNoConfigurada>();
    }

    [Fact]
    public void UnaFuenteRegistradaAntesNoSeReemplaza()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IClsFuenteMargen, clsFuenteDePrueba>();

        servicios.AddInfraestructura(new ConfigurationBuilder().Build());

        servicios.Should().ContainSingle(d => d.ServiceType == typeof(IClsFuenteMargen))
            .Which.ImplementationType.Should().Be<clsFuenteDePrueba>();
    }

    [Theory]
    [InlineData(null, "50000005")]
    [InlineData("50000010", "50000010")]
    public void LaOrganizacionDeVentasPorDefectoSeLeeDeSapTm(string? configurada, string esperada)
    {
        using var proveedor = Proveedor(configurada);

        proveedor.GetRequiredService<IOptions<clsOpcionesMargen>>().Value.SalesOrgDefault.Should().Be(esperada);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123")]
    public void UnaOrganizacionVaciaOLargaNoPasaLaValidacion(string configurada)
    {
        using var proveedor = Proveedor(configurada);

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesMargen>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("SalesOrgDefault");
    }

    private static ServiceProvider Proveedor(string? salesOrgDefault)
    {
        var valores = new Dictionary<string, string?>();
        if (salesOrgDefault is not null)
        {
            valores["SapTm:SalesOrgDefault"] = salesOrgDefault;
        }

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        return servicios.BuildServiceProvider();
    }

    private sealed class clsFuenteDePrueba : IClsFuenteMargen
    {
        public string Modo => "prueba";

        public bool EstaConfigurada => true;

        public Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(null);
    }
}
