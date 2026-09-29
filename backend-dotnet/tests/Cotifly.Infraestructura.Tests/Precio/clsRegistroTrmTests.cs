using System.Net;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Precio;

/// <summary>Registro de la TRM en <c>AddInfraestructura</c> (HU-012.1): caché por día, tubería de resiliencia y opciones validadas.</summary>
public sealed class clsRegistroTrmTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ElProveedorEsLaCacheSobreLaSuperfinancieraYEsUnico()
    {
        using var proveedor = Proveedor([]);

        var trm = proveedor.GetRequiredService<IClsProveedorTrm>();

        trm.Should().BeOfType<clsProveedorTrmEnCache>();
        proveedor.GetRequiredService<IClsProveedorTrm>().Should().BeSameAs(trm);
    }

    [Fact]
    public void LasOpcionesPorDefectoApuntanAlConjuntoOficial()
    {
        using var proveedor = Proveedor([]);

        var opciones = proveedor.GetRequiredService<IOptions<clsOpcionesTrm>>().Value;

        opciones.Url.Should().Be("https://www.datos.gov.co/resource/32sa-8pi3.json");
        opciones.TiempoEsperaSegundos.Should().Be(10);
    }

    [Theory]
    [InlineData("Trm:Url", "ftp://datos.gov.co/x.json", "Trm:Url")]
    [InlineData("Trm:Url", "https://datos.gov.co/x.json?$limit=1", "Trm:Url")]
    [InlineData("Trm:Url", "no-es-url", "Trm:Url")]
    [InlineData("Trm:TiempoEsperaSegundos", "0", "TiempoEsperaSegundos")]
    [InlineData("Trm:TiempoEsperaSegundos", "600", "TiempoEsperaSegundos")]
    public void UnaOpcionInvalidaImpideArrancar(string clave, string valor, string mensaje)
    {
        using var proveedor = Proveedor(new() { [clave] = valor });

        var accion = () => proveedor.GetRequiredService<IOptions<clsOpcionesTrm>>().Value;

        accion.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(mensaje);
    }

    [Fact]
    public async Task ReintentaUnErrorTransitorioYGuardaLaTrmEnCache()
    {
        var fuente = new clsTrmFalso().Responder(HttpStatusCode.ServiceUnavailable);
        using var proveedor = Proveedor(new() { ["Trm:Url"] = clsTrmFalso.Url }, fuente);
        var trm = proveedor.GetRequiredService<IClsProveedorTrm>();

        var primera = await trm.ObtenerAsync(new DateOnly(2026, 9, 27), Ct);
        var segunda = await trm.ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        primera.Valor.Valor.Should().Be(3306.86m);
        segunda.Valor.Should().Be(primera.Valor);
        fuente.Peticiones.Should().HaveCount(2, "un 503 y el reintento; la segunda consulta sale de la caché");
    }

    [Fact]
    public async Task AgotadosLosReintentosResponderTrmNoDisponible()
    {
        var fuente = new clsTrmFalso().Caida();
        using var proveedor = Proveedor(new() { ["Trm:Url"] = clsTrmFalso.Url }, fuente);

        var resultado = await proveedor.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        fuente.Peticiones.Should().HaveCount(1 + clsProveedorTrmSuperfinanciera.Reintentos);
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, clsTrmFalso? fuente = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        if (fuente is not null)
        {
            servicios.AddHttpClient(clsProveedorTrmSuperfinanciera.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => fuente);
        }

        return servicios.BuildServiceProvider();
    }
}
