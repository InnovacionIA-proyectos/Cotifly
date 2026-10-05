using System.Net;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Precio;

/// <summary>
/// Registro de la TRM en <c>AddInfraestructura</c> (HU-012.1, ADR-014): cadena según <c>Trm:Proveedor</c> (por defecto DolarApi con respaldo
/// en la Superintendencia), caché por día, tubería de resiliencia y opciones validadas. Las dos fuentes son falsas: nunca sale a la red.
/// </summary>
public sealed class clsRegistroTrmTests : IDisposable
{
    // 10:00 del lunes 2026-10-05 en Bogotá: la publicación por defecto del DolarApi falso es de ese día.
    private static readonly DateOnly _lunes = new(2026, 10, 5);
    private readonly clsTrmFalso _superfinanciera = new();
    private readonly clsDolarApiFalso _dolarApi = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ElProveedorEsLaCacheSobreLaCadenaYEsUnico()
    {
        using var proveedor = Proveedor([]);

        var trm = proveedor.GetRequiredService<IClsProveedorTrm>();

        trm.Should().BeOfType<clsProveedorTrmEnCache>();
        proveedor.GetRequiredService<IClsProveedorTrm>().Should().BeSameAs(trm);
    }

    [Fact]
    public void LasOpcionesPorDefectoSonDolarApiConRespaldoEnElConjuntoOficial()
    {
        using var proveedor = Proveedor([], falsas: false);

        var opciones = proveedor.GetRequiredService<IOptions<clsOpcionesTrm>>().Value;

        opciones.Proveedor.Should().Be("dolarapi_con_respaldo");
        opciones.UrlDolarApi.Should().Be("https://co.dolarapi.com/v1/trm");
        opciones.Url.Should().Be("https://www.datos.gov.co/resource/32sa-8pi3.json");
        opciones.TiempoEsperaSegundos.Should().Be(10);
    }

    [Theory]
    [InlineData("Trm:Url", "ftp://datos.gov.co/x.json", "Trm:Url")]
    [InlineData("Trm:Url", "https://datos.gov.co/x.json?$limit=1", "Trm:Url")]
    [InlineData("Trm:Url", "no-es-url", "Trm:Url")]
    [InlineData("Trm:UrlDolarApi", "ftp://co.dolarapi.com/v1/trm", "Trm:UrlDolarApi")]
    [InlineData("Trm:UrlDolarApi", "https://co.dolarapi.com/v1/trm?x=1", "Trm:UrlDolarApi")]
    [InlineData("Trm:UrlDolarApi", "https://u:p@co.dolarapi.com/v1/trm", "Trm:UrlDolarApi")]
    [InlineData("Trm:Proveedor", "banrep", "Trm:Proveedor")]
    [InlineData("Trm:Proveedor", "", "Proveedor")]
    [InlineData("Trm:TiempoEsperaSegundos", "0", "TiempoEsperaSegundos")]
    [InlineData("Trm:TiempoEsperaSegundos", "600", "TiempoEsperaSegundos")]
    public void UnaOpcionInvalidaImpideArrancar(string clave, string valor, string mensaje)
    {
        using var proveedor = Proveedor(new() { [clave] = valor });

        var accion = () => proveedor.GetRequiredService<IOptions<clsOpcionesTrm>>().Value;

        accion.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(mensaje);
    }

    [Theory]
    [InlineData("superfinanciera")]
    [InlineData(" DolarApi ")]
    [InlineData("DOLARAPI_CON_RESPALDO")]
    public void ElProveedorSeAceptaSinDistinguirMayusculasNiEspacios(string valor)
    {
        using var proveedor = Proveedor(new() { ["Trm:Proveedor"] = valor });

        proveedor.GetRequiredService<IOptions<clsOpcionesTrm>>().Value.ProveedorNormalizado.Should().Be(valor.Trim().ToLowerInvariant());
    }

    [Fact]
    public async Task PorDefectoLaTrmSaleDeDolarApiSinConsultarLaSuperintendencia()
    {
        using var proveedor = Proveedor([]);

        var trm = await proveedor.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(_lunes, Ct);

        trm.Valor.Valor.Should().Be(3273.49m);
        trm.Valor.Fuente.Should().Be(clsProveedorTrmDolarApi.Fuente);
        _dolarApi.Peticiones.Should().ContainSingle();
        _superfinanciera.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConDolarApiCaidoLaTrmSaleDelRespaldo()
    {
        _dolarApi.Caida(1 + clsProveedorTrmSuperfinanciera.Reintentos);
        _superfinanciera.Responder(HttpStatusCode.OK, "[" + clsTrmFalso.Fila(3280.10m, _lunes, _lunes) + "]");
        using var proveedor = Proveedor([]);

        var trm = await proveedor.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(_lunes, Ct);

        trm.Valor.Valor.Should().Be(3280.10m);
        trm.Valor.Fuente.Should().Be(clsProveedorTrmSuperfinanciera.Fuente);
        _dolarApi.Peticiones.Should().HaveCount(1 + clsProveedorTrmSuperfinanciera.Reintentos, "DolarApi también reintenta los transitorios");
    }

    [Fact]
    public async Task ConAmbasFuentesCaidasEsTrmNoDisponible()
    {
        _dolarApi.Caida(1 + clsProveedorTrmSuperfinanciera.Reintentos);
        _superfinanciera.Caida(1 + clsProveedorTrmSuperfinanciera.Reintentos);
        using var proveedor = Proveedor([]);

        var resultado = await proveedor.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(_lunes, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Detalle.Should().StartWith("principal: HTTP 503").And.Contain("respaldo: HTTP 503");
    }

    [Fact]
    public async Task ConSoloDolarApiNoHayRespaldo()
    {
        _dolarApi.Caida(1 + clsProveedorTrmSuperfinanciera.Reintentos);
        using var proveedor = Proveedor(new() { ["Trm:Proveedor"] = "dolarapi" });

        var resultado = await proveedor.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(_lunes, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        _superfinanciera.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ReintentaUnErrorTransitorioDeLaSuperintendenciaYGuardaLaTrmEnCache()
    {
        _superfinanciera.Responder(HttpStatusCode.ServiceUnavailable);
        using var proveedor = Proveedor(new() { ["Trm:Proveedor"] = "superfinanciera" });
        var trm = proveedor.GetRequiredService<IClsProveedorTrm>();

        var primera = await trm.ObtenerAsync(new DateOnly(2026, 9, 27), Ct);
        var segunda = await trm.ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        primera.Valor.Valor.Should().Be(3306.86m);
        segunda.Valor.Should().Be(primera.Valor);
        _superfinanciera.Peticiones.Should().HaveCount(2, "un 503 y el reintento; la segunda consulta sale de la caché");
        _dolarApi.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task AgotadosLosReintentosDeLaSuperintendenciaRespondeTrmNoDisponible()
    {
        _superfinanciera.Caida();
        using var proveedor = Proveedor(new() { ["Trm:Proveedor"] = "superfinanciera" });

        var resultado = await proveedor.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        _superfinanciera.Peticiones.Should().HaveCount(1 + clsProveedorTrmSuperfinanciera.Reintentos);
    }

    public void Dispose()
    {
        _superfinanciera.Dispose();
        _dolarApi.Dispose();
    }

    private ServiceProvider Proveedor(Dictionary<string, string?> valores, bool falsas = true)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        var reloj = new clsRelojFalso { AhoraUtc = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero) };
        servicios.AddSingleton<IClsReloj>(reloj);
        if (falsas)
        {
            valores.TryAdd("Trm:Url", clsTrmFalso.Url);
            valores.TryAdd("Trm:UrlDolarApi", clsDolarApiFalso.Url);
        }

        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        servicios.Replace(ServiceDescriptor.Singleton<IClsReloj>(reloj));
        if (falsas)
        {
            servicios.AddHttpClient(clsProveedorTrmSuperfinanciera.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => _superfinanciera);
            servicios.AddHttpClient(clsProveedorTrmDolarApi.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => _dolarApi);
        }

        return servicios.BuildServiceProvider();
    }
}
