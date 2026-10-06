using System.Net;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
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

/// <summary>
/// Selección de la fuente SAP TM por <c>SapTm:Modo</c> (port de <c>construir_resolver</c>, <c>mcp_server/margen/servicio.py</c>),
/// validación de <see cref="clsOpcionesSapTm"/> al arrancar y tubería de resiliencia del cliente OData.
/// </summary>
public sealed class clsRegistroFuenteMargenTests
{
    private const string ClaveValida = "Kq7-Vr2m-Xz9p";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinModoLaFuenteEsNoConfigurada(string? modo)
    {
        using var proveedor = Proveedor(new() { ["SapTm:Modo"] = modo });

        var fuente = proveedor.GetRequiredService<IClsFuenteMargen>();

        fuente.Should().BeOfType<clsFuenteMargenNoConfigurada>();
        fuente.EstaConfigurada.Should().BeFalse();
    }

    // test_construir_resolver_export
    [Theory]
    [InlineData("export_json")]
    [InlineData(" EXPORT_JSON ")]
    public void ConModoExportJsonLaFuenteEsElExport(string modo)
    {
        using var proveedor = Proveedor(new() { ["SapTm:Modo"] = modo, ["SapTm:ExportPath"] = clsSapTmFalso.RutaMuestra });

        var fuente = proveedor.GetRequiredService<IClsFuenteMargen>();

        fuente.Should().BeOfType<clsFuenteMargenExportJson>();
        fuente.Modo.Should().Be("export_json");
        fuente.EstaConfigurada.Should().BeTrue();
    }

    [Fact]
    public void ElExportEsUnaSolaInstanciaParaConservarSuCache()
    {
        using var proveedor = Proveedor(new() { ["SapTm:Modo"] = "export_json", ["SapTm:ExportPath"] = clsSapTmFalso.RutaMuestra });

        IClsFuenteMargen Resolver()
        {
            using var ambito = proveedor.CreateScope();
            return ambito.ServiceProvider.GetRequiredService<IClsFuenteMargen>();
        }

        Resolver().Should().BeSameAs(Resolver());
    }

    // test_construir_resolver_odata
    [Theory]
    [InlineData("odata")]
    [InlineData("OData")]
    public void ConModoODataLaFuenteEsElClienteOData(string modo)
    {
        using var proveedor = Proveedor(ConfiguracionOData(modo));

        var fuente = proveedor.GetRequiredService<IClsFuenteMargen>();

        fuente.Should().BeOfType<clsFuenteMargenOData>();
        fuente.Modo.Should().Be("odata");
        fuente.EstaConfigurada.Should().BeTrue();
    }

    [Fact]
    public void UnModoDesconocidoNoPasaLaValidacion()
    {
        using var proveedor = Proveedor(new() { ["SapTm:Modo"] = "drive" });

        LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("SapTm:Modo");
    }

    [Fact]
    public void ExportJsonExigeLaRutaDelExport()
    {
        using var proveedor = Proveedor(new() { ["SapTm:Modo"] = "export_json" });

        LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("SapTm:ExportPath");
    }

    [Theory]
    [InlineData("SapTm:Url")]
    [InlineData("SapTm:User")]
    [InlineData("SapTm:Pass")]
    public void ODataExigeUrlUsuarioYClaveEnCualquierEntorno(string falta)
    {
        var configuracion = ConfiguracionOData();
        configuracion[falta] = null;
        using var proveedor = Proveedor(configuracion);

        LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(falta);
    }

    [Theory]
    [InlineData("sap-tm.local/sap/opu/odata")]
    [InlineData("ftp://sap-tm.local/odata")]
    [InlineData("http://svc_cotifly:clave@sap-tm.local/odata")]
    [InlineData("http://sap-tm.local/odata?sap-client=100")]
    public void LaUrlDeODataDebeSerHttpAbsolutaSinCredencialesNiConsulta(string url)
    {
        var configuracion = ConfiguracionOData();
        configuracion["SapTm:Url"] = url;
        using var proveedor = Proveedor(configuracion);

        var mensaje = LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message;
        mensaje.Should().Contain("SapTm:Url").And.NotContain("clave@");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("	")]
    public void UnaClaveSoloConEspaciosNoPasaLaValidacion(string clave)
    {
        var configuracion = ConfiguracionOData();
        configuracion["SapTm:Pass"] = clave;
        using var proveedor = Proveedor(configuracion);

        LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("SapTm:Pass");
    }

    [Theory]
    [InlineData("10")]
    [InlineData("1000")]
    [InlineData("abc")]
    [InlineData("１００")]
    public void UnSapClientQueNoEsDeTresDigitosNoPasaLaValidacion(string mandante)
    {
        var configuracion = ConfiguracionOData();
        configuracion["SapTm:SapClient"] = mandante;
        using var proveedor = Proveedor(configuracion);

        LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("SapTm:SapClient");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("100")]
    public void UnSapClientVacioODeTresDigitosEsValido(string? mandante)
    {
        var configuracion = ConfiguracionOData();
        configuracion["SapTm:SapClient"] = mandante;
        using var proveedor = Proveedor(configuracion);

        LeerOpciones(proveedor).Should().NotThrow();
    }

    [Theory]
    [InlineData("<secreto>")]
    [InlineData("cambiar")]
    [InlineData("corta")]
    public void EnProduccionUnaClaveDeEjemploOCortaImpideArrancarSinMostrarla(string clave)
    {
        var configuracion = ConfiguracionOData();
        configuracion["SapTm:Pass"] = clave;
        using var proveedor = Proveedor(configuracion, Environments.Production);

        var mensaje = LeerOpciones(proveedor).Should().Throw<OptionsValidationException>().Which.Message;
        mensaje.Should().Contain("SapTm:Pass").And.NotContain($"'{clave}'");
    }

    [Fact]
    public void EnProduccionConClaveRealArranca()
    {
        using var proveedor = Proveedor(ConfiguracionOData(), Environments.Production);

        LeerOpciones(proveedor).Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("export_json")]
    public void EnProduccionSinODataNoSeExigenCredencialesDeSap(string? modo)
    {
        using var proveedor = Proveedor(
            new() { ["SapTm:Modo"] = modo, ["SapTm:ExportPath"] = modo is null ? null : clsSapTmFalso.RutaMuestra },
            Environments.Production);

        LeerOpciones(proveedor).Should().NotThrow();
    }

    [Fact]
    public async Task ElClienteODataReintentaUnErrorTransitorio()
    {
        var sap = new clsSapTmFalso()
            .Responder(HttpStatusCode.ServiceUnavailable)
            .ResponderFilas(new { SalesOrgId = "50000005", TrqType = "OPTN", OrderPartyId = "15927", Value = "13.5" });
        using var proveedor = Proveedor(ConfiguracionOData(), configurar: s => UsarSapFalso(s, sap));

        var valor = await proveedor.GetRequiredService<IClsFuenteMargen>().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        valor.Should().Be(13.5m);
        sap.Peticiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElClienteODataAgotaLosReintentosYReportaFuenteNoDisponible()
    {
        var sap = new clsSapTmFalso();
        for (var i = 0; i < 5; i++)
        {
            sap.Responder(HttpStatusCode.InternalServerError);
        }

        using var proveedor = Proveedor(ConfiguracionOData(), configurar: s => UsarSapFalso(s, sap));

        var consultar = () => proveedor.GetRequiredService<IClsFuenteMargen>().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();
        sap.Peticiones.Should().HaveCount(1 + clsFuenteMargenOData.Reintentos);
    }

    [Fact]
    public async Task ElClienteODataNoReintentaUnErrorQueNoEsTransitorio()
    {
        var sap = new clsSapTmFalso().Responder(HttpStatusCode.Unauthorized).Responder(HttpStatusCode.OK, "{\"d\":{\"results\":[]}}");
        using var proveedor = Proveedor(ConfiguracionOData(), configurar: s => UsarSapFalso(s, sap));

        var consultar = () => proveedor.GetRequiredService<IClsFuenteMargen>().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();
        sap.Peticiones.Should().ContainSingle();
    }

    [Fact]
    public async Task UnFalloDeSapDejaUnaSolaLineaDeWarningSinTrazaNiEventosDePolly()
    {
        var sap = new clsSapTmFalso();
        for (var i = 0; i <= clsFuenteMargenOData.Reintentos; i++)
        {
            sap.Responder(HttpStatusCode.InternalServerError);
        }

        using var logs = new clsProveedorLogsFalso();
        using var proveedor = Proveedor(ConfiguracionOData(), configurar: s =>
        {
            UsarSapFalso(s, sap);
            s.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        });

        var consultar = () => proveedor.GetRequiredService<IClsFuenteMargen>().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);
        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();

        var avisos = logs.Registros.Where(r => r.Registro.Nivel >= LogLevel.Warning).ToList();
        avisos.Should().ContainSingle().Which.Should().Match<(string Categoria, clsRegistroLog Registro)>(r =>
            r.Categoria == typeof(clsFuenteMargenOData).FullName && r.Registro.Excepcion == null);
        logs.Registros.Where(r => r.Categoria.StartsWith("Polly", StringComparison.Ordinal))
            .Should().NotBeEmpty("los eventos de la tubería se siguen emitiendo")
            .And.OnlyContain(r => r.Registro.Nivel <= LogLevel.Debug, "pero en Debug");
    }

    [Fact]
    public void LosTiemposDeLaTuberiaRespetanElLimiteTotalDelPython()
    {
        clsFuenteMargenOData.TiempoTotal.Should().Be(TimeSpan.FromSeconds(10));
        clsFuenteMargenOData.TiempoPorIntento.Should().BeLessThan(clsFuenteMargenOData.TiempoTotal);
    }

    private static Dictionary<string, string?> ConfiguracionOData(string modo = "odata") => new()
    {
        ["SapTm:Modo"] = modo,
        ["SapTm:Url"] = clsSapTmFalso.UrlBase,
        ["SapTm:User"] = "svc_cotifly",
        ["SapTm:Pass"] = ClaveValida,
    };

    private static void UsarSapFalso(IServiceCollection servicios, clsSapTmFalso sap) =>
        servicios.AddHttpClient(clsFuenteMargenOData.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => sap);

    private static Func<clsOpcionesSapTm> LeerOpciones(ServiceProvider proveedor) =>
        () => proveedor.GetRequiredService<IOptions<clsOpcionesSapTm>>().Value;

    private static ServiceProvider Proveedor(
        Dictionary<string, string?> valores, string entorno = "Development", Action<IServiceCollection>? configurar = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso(entorno));
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }
}
