using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// Registro de la fuente del margen de SAP TM (la copia local, sin modos) y de las opciones de la sección <c>SapTm</c>
/// (<c>SalesOrgDefault</c> y <c>AvisoSincronizacionHoras</c>). La lectura de la tabla se prueba contra SQL Server en
/// <c>clsFuenteMargenTablaLocalIntegracionTests</c>.
/// </summary>
public sealed class clsRegistroFuenteMargenTests
{
    [Fact]
    public void LaFuenteDeMargenEsLaTablaLocalConElAlcanceDeLaPeticion()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(new ConfigurationBuilder().Build());

        var registro = servicios.Should().ContainSingle(d => d.ServiceType == typeof(IClsFuenteMargen)).Subject;
        registro.ImplementationType.Should().Be<clsFuenteMargenTablaLocal>();
        registro.Lifetime.Should().Be(ServiceLifetime.Scoped, "lee con el DbContext de la petición");
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
    [InlineData(" 0000000580 ", "580")]
    [InlineData("580", "580")]
    [InlineData("15927", "15927")]
    [InlineData("1000", "1000")]
    [InlineData("000", "0")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ElBpSeComparaRecortadoYSinCerosALaIzquierda(string? bp, string esperado) =>
        clsFuenteMargenTablaLocal.NormalizarBp(bp).Should().Be(esperado);

    [Theory]
    [InlineData(null, "50000005")]
    [InlineData("50000010", "50000010")]
    public void LaOrganizacionDeVentasPorDefectoSeLeeDeSapTm(string? configurada, string esperada)
    {
        using var proveedor = Proveedor(new() { ["SapTm:SalesOrgDefault"] = configurada });

        proveedor.GetRequiredService<IOptions<clsOpcionesMargen>>().Value.SalesOrgDefault.Should().Be(esperada);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123")]
    public void UnaOrganizacionVaciaOLargaNoPasaLaValidacion(string configurada)
    {
        using var proveedor = Proveedor(new() { ["SapTm:SalesOrgDefault"] = configurada });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesMargen>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("SalesOrgDefault");
    }

    // Umbral del aviso de sincronización vieja; 48 h por defecto y siempre mayor que cero.
    [Theory]
    [InlineData(null, 48)]
    [InlineData("72", 72)]
    [InlineData("1", 1)]
    public void ElUmbralDelAvisoDeSincronizacionSeLeeDeSapTm(string? configurado, int esperado)
    {
        using var proveedor = Proveedor(new() { ["SapTm:AvisoSincronizacionHoras"] = configurado });

        proveedor.GetRequiredService<IOptions<clsOpcionesMargen>>().Value.AvisoSincronizacionHoras.Should().Be(esperado);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void UnUmbralDeCeroONegativoNoPasaLaValidacion(string configurado)
    {
        using var proveedor = Proveedor(new() { ["SapTm:AvisoSincronizacionHoras"] = configurado });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesMargen>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("AvisoSincronizacionHoras");
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddInfraestructura(new ConfigurationBuilder()
            .AddInMemoryCollection(valores.Where(v => v.Value is not null))
            .Build());
        return servicios.BuildServiceProvider();
    }

    private sealed class clsFuenteDePrueba : IClsFuenteMargen
    {
        public string Modo => "prueba";

        public Task<clsConsultaMargenSap> ConsultarAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default) =>
            Task.FromResult(clsConsultaMargenSap.NoConfigurada);
    }
}
