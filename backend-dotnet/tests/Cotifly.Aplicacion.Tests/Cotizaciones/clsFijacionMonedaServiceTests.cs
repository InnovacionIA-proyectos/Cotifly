using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary><see cref="clsFijacionMonedaService"/> (ADR-014 §5): moneda y TRM que quedan fijas al crear una cotización.</summary>
public sealed class clsFijacionMonedaServiceTests
{
    private static readonly DateOnly _hoy = new(2026, 10, 5);
    private readonly IClsProveedorTrm _trm = Substitute.For<IClsProveedorTrm>();
    private readonly clsFijacionMonedaService _servicio;

    public clsFijacionMonedaServiceTests()
    {
        var calendario = Substitute.For<IClsCalendarioNegocio>();
        calendario.Hoy.Returns(_hoy);
        _servicio = new clsFijacionMonedaService(new clsPrecioService(_trm, calendario));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("COP")]
    [InlineData(" cop ")]
    public async Task EnPesosLaTrmEsCeroSinConsultarLaFuente(string? moneda)
    {
        (await _servicio.FijarAsync(moneda, Ct)).Valor.Should().Be(new clsMonedaFijada("COP", 0m));
        await _trm.DidNotReceive().ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnDolaresFijaLaTrmDelDia()
    {
        _trm.ObtenerAsync(_hoy, Arg.Any<CancellationToken>()).Returns(new clsTrm(3273.49m, _hoy, _hoy, "dolarapi"));

        (await _servicio.FijarAsync(" usd ", Ct)).Valor.Should().Be(new clsMonedaFijada("USD", 3273.49m));
    }

    [Fact]
    public async Task SinTrmEsTrmNoDisponible()
    {
        _trm.ObtenerAsync(_hoy, Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "caída"));

        (await _servicio.FijarAsync("USD", Ct)).Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
    }

    [Fact]
    public async Task OtraMonedaEsPrecioInvalido() =>
        (await _servicio.FijarAsync("EUR", Ct)).Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
}
