using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Configuracion;

/// <summary>
/// <c>Resultado:MotivosPerdida</c> y <c>Resultado:MotivoConComentario</c> se enlazan desde la configuración (la
/// lista configurada reemplaza a la de Nexo) y una configuración inválida no deja arrancar.
/// </summary>
public sealed class clsValidadorOpcionesResultadoTests
{
    [Fact]
    public void SinConfigurarEsLaListaDeNexoYEsValida()
    {
        var opciones = Opciones([]);

        opciones.Motivos.Should().Equal(clsOpcionesResultado.MotivosPorDefecto);
        opciones.ConComentario.Should().Be("Otro");
        new clsValidadorOpcionesResultado().Validate(null, opciones).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void LaListaConfiguradaReemplazaALaDeNexo()
    {
        var opciones = Opciones(new()
        {
            ["Resultado:MotivosPerdida:0"] = "Plazo",
            ["Resultado:MotivosPerdida:1"] = "Otra razón",
            ["Resultado:MotivoConComentario"] = "Otra razón",
        });

        opciones.Motivos.Should().Equal("Plazo", "Otra razón");
        new clsValidadorOpcionesResultado().Validate(null, opciones).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Resultado:MotivoConComentario", "Ninguno")]
    [InlineData("Resultado:MotivosPerdida:0", "Un motivo que tiene más de cien caracteres porque explica con demasiado detalle por qué se perdió la cotización")]
    public void UnaConfiguracionInvalidaNoArranca(string clave, string valor)
    {
        var resultado = new clsValidadorOpcionesResultado().Validate(null, Opciones(new() { [clave] = valor }));

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("Resultado:");
    }

    private static clsOpcionesResultado Opciones(Dictionary<string, string?> valores)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        using var proveedor = new ServiceCollection()
            .Configure<clsOpcionesResultado>(configuracion.GetSection(clsOpcionesResultado.Seccion))
            .BuildServiceProvider();
        return proveedor.GetRequiredService<IOptions<clsOpcionesResultado>>().Value;
    }
}
