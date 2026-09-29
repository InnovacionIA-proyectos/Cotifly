using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Identidad;

/// <summary>
/// Registro de la regional (<c>DimEjecutivo</c>, #81 y ADR-016 §6): opciones por defecto de la tabla real, límite del login y
/// validación de la sección <c>Regional</c>. Sin base de datos: la conexión no se abre.
/// </summary>
public sealed class clsRegistroRegionalTests
{
    private const string CadenaFalsa = "Server=127.0.0.1,1;Database=DWCubosEBSSAP;User Id=lector;Password=NoImporta123!;Encrypt=False";

    [Fact]
    public void LasOpcionesPorDefectoSonLasDeLaTablaRealConElUsuarioConDominio()
    {
        using var proveedor = Proveedor([]);

        var opciones = proveedor.GetRequiredService<IOptions<clsOpcionesRegional>>().Value;

        opciones.Esquema.Should().Be("dbo");
        opciones.Tabla.Should().Be("DimEjecutivo");
        opciones.ColumnaUsuario.Should().Be("USUARIO");
        opciones.ColumnaRegional.Should().Be("REGIONAL");
        opciones.Dominio.Should().Be("boterosoto");
        opciones.TiempoEsperaSegundos.Should().Be(10);
        opciones.TiempoEsperaLoginSegundos.Should().Be(3);
        opciones.LimiteLogin.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void ElLimiteDelLoginEsElMenorEntreLosDosTiemposDeEspera()
    {
        new clsOpcionesRegional { TiempoEsperaSegundos = 2, TiempoEsperaLoginSegundos = 5 }.LimiteLogin.Should().Be(TimeSpan.FromSeconds(2));
        new clsOpcionesRegional { TiempoEsperaSegundos = 30, TiempoEsperaLoginSegundos = 1 }.LimiteLogin.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ConCadenaElDirectorioEsDimEjecutivoConElLimiteConfigurado()
    {
        using var proveedor = Proveedor(new() { ["ConnectionStrings:Regional"] = CadenaFalsa, ["Regional:TiempoEsperaLoginSegundos"] = "2" });
        using var ambito = proveedor.CreateScope();

        var directorio = ambito.ServiceProvider.GetRequiredService<IClsDirectorioEjecutivos>();

        directorio.Should().BeOfType<clsDirectorioEjecutivosDimEjecutivo>();
        directorio.Configurado.Should().BeTrue();
        directorio.LimiteLogin.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SinCadenaElDirectorioNoConsultaNada()
    {
        using var proveedor = Proveedor([]);
        using var ambito = proveedor.CreateScope();

        var directorio = ambito.ServiceProvider.GetRequiredService<IClsDirectorioEjecutivos>();

        directorio.Should().BeOfType<clsDirectorioEjecutivosNoDisponible>();
        directorio.Configurado.Should().BeFalse();
    }

    [Theory]
    [InlineData("Regional:ColumnaUsuario", "USUARIO; DROP TABLE x", "ColumnaUsuario")]
    [InlineData("Regional:ColumnaUsuario", "boterosoto\\USUARIO", "ColumnaUsuario")]
    [InlineData("Regional:Tabla", "", "Tabla")]
    [InlineData("Regional:Dominio", "boterosoto\\", "Dominio")]
    [InlineData("Regional:Dominio", "otro dominio", "Dominio")]
    [InlineData("Regional:TiempoEsperaSegundos", "1", "TiempoEsperaSegundos")]
    [InlineData("Regional:TiempoEsperaLoginSegundos", "0", "TiempoEsperaLoginSegundos")]
    [InlineData("Regional:TiempoEsperaLoginSegundos", "30", "TiempoEsperaLoginSegundos")]
    public void UnaOpcionInvalidaImpideArrancar(string clave, string valor, string mensaje)
    {
        using var proveedor = Proveedor(new() { [clave] = valor });

        var accion = () => proveedor.GetRequiredService<IOptions<clsOpcionesRegional>>().Value;

        accion.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(mensaje);
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        return servicios.BuildServiceProvider();
    }
}
