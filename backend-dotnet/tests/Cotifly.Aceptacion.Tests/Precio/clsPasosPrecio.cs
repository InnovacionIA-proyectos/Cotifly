using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Precio;

/// <summary>Pasos de <c>specs/features/precio.feature</c> (caso de uso real resuelto del contenedor de dependencias).</summary>
[Binding]
public sealed class clsPasosPrecio(clsContextoEscenario contexto)
{
    private clsResultado<clsSalidaPrecio>? _resultado;

    [When("calculo el precio de un costo de {int} con margen {string} por el método {string}")]
    public async Task CuandoCalculoElPrecio(int costo, string margen, string metodo) =>
        _resultado = await contexto.ConsultarAsync(s => Task.FromResult(
            s.GetRequiredService<IClsPrecioService>().Calcular(costo, Fraccion(margen), metodo)));

    [Then("el precio es {int} y la utilidad {int}")]
    public void EntoncesElPrecioYLaUtilidad(int precio, int utilidad)
    {
        var salida = Salida();
        salida.Precio.Should().Be(precio);
        salida.Utilidad.Should().Be(utilidad);
    }

    [Then("el precio es {int}")]
    public void EntoncesElPrecio(int precio) => Salida().Precio.Should().Be(precio);

    [Then("el cálculo se rechaza con el código {string} y un mensaje que contiene {string}")]
    public void EntoncesSeRechaza(string codigo, string texto)
    {
        _resultado.Should().NotBeNull();
        _resultado!.EsFallo.Should().BeTrue();
        _resultado.Error.Codigo.Should().Be(codigo);
        _resultado.Error.Mensaje.Should().Contain(texto);
    }

    private clsSalidaPrecio Salida()
    {
        _resultado.Should().NotBeNull();
        _resultado!.EsExitoso.Should().BeTrue(_resultado.EsFallo ? _resultado.Error.Mensaje : string.Empty);
        return _resultado.Valor;
    }

    internal static decimal Fraccion(string texto) => decimal.Parse(texto, NumberStyles.Float, CultureInfo.InvariantCulture);
}
