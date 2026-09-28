using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Precio;

/// <summary>
/// <see cref="clsPrecioService"/> adapta la regla pura al contrato <c>SalidaPrecio</c> y, en USD, convierte con la TRM del día de negocio
/// (HU-012.2). Los casos de paridad de <c>tests/test_precio.py</c> están en <c>Cotifly.Dominio.Tests/Precio/clsCalculadoraPrecioTests</c>.
/// </summary>
public sealed class clsPrecioServiceTests
{
    private static readonly DateOnly _hoy = new(2026, 9, 28);

    private readonly IClsProveedorTrm _trm = Substitute.For<IClsProveedorTrm>();
    private readonly IClsCalendarioNegocio _calendario = Substitute.For<IClsCalendarioNegocio>();
    private readonly clsPrecioService _servicio;

    public clsPrecioServiceTests()
    {
        _calendario.Hoy.Returns(_hoy);
        _trm.ObtenerAsync(_hoy, Arg.Any<CancellationToken>()).Returns(new clsTrm(3306.86m, new DateOnly(2026, 9, 26), _hoy, "prueba"));
        _servicio = new clsPrecioService(_trm, _calendario);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DevuelveLaSalidaDelContrato() =>
        _servicio.Calcular(2_850_000m, 0.135m, "margen").Valor
            .Should().Be(new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m));

    [Fact]
    public void SinMetodoUsaMargen() =>
        _servicio.Calcular(2_850_000m, 0.135m).Valor.Metodo.Should().Be("margen");

    [Fact]
    public void UnaEntradaInvalidaDevuelvePrecioInvalido() =>
        _servicio.Calcular(1_000_000m, 1m, "margen").Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("COP")]
    [InlineData(" cop ")]
    public async Task EnPesosNoConsultaLaTrmYLaDejaEnCero(string? moneda)
    {
        var salida = (await _servicio.CalcularAsync(2_850_000m, 0.135m, "margen", moneda, Ct)).Valor;

        salida.Should().Be(new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m, "COP", 0m, null));
        await _trm.DidNotReceive().ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnDolaresConvierteElPrecioEnPesosConLaTrmDelDiaDeNegocio()
    {
        var salida = (await _servicio.CalcularAsync(2_850_000m, 0.135m, "margen", "usd", Ct)).Valor;

        // 3.294.798 / 3.306,86 = 996,35 (half-to-even a 2 decimales); costo y utilidad siguen en pesos.
        salida.Precio.Should().Be(996.35m);
        salida.Moneda.Should().Be("USD");
        salida.TrmAplicada.Should().Be(3306.86m);
        salida.PrecioCop.Should().Be(3_294_798m);
        salida.Costo.Should().Be(2_850_000m);
        salida.Utilidad.Should().Be(444_798m);
        salida.Margen.Should().Be(0.135m);
        await _trm.Received(1).ObtenerAsync(_hoy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnDolaresConMarkupConvierteElPrecioDeMarkup()
    {
        var salida = (await _servicio.CalcularAsync(2_850_000m, 0.135m, "markup", "USD", Ct)).Valor;

        salida.PrecioCop.Should().Be(3_234_750m);
        salida.Precio.Should().Be(978.19m); // 3.234.750 / 3.306,86 = 978,1938...
        salida.Metodo.Should().Be("markup");
    }

    [Fact]
    public async Task SiLaTrmFallaNoHayPrecioEnDolares()
    {
        _trm.ObtenerAsync(_hoy, Arg.Any<CancellationToken>()).Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "La TRM no está disponible."));

        var resultado = await _servicio.CalcularAsync(2_850_000m, 0.135m, "margen", "USD", Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
    }

    [Fact]
    public async Task UnaEntradaInvalidaNoConsultaLaTrm()
    {
        var resultado = await _servicio.CalcularAsync(0m, 0.135m, "margen", "USD", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        await _trm.DidNotReceive().ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("dólares")]
    public async Task UnaMonedaNoSoportadaEsPrecioInvalido(string moneda)
    {
        var resultado = await _servicio.CalcularAsync(2_850_000m, 0.135m, "margen", moneda, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        resultado.Error.Mensaje.Should().Contain("COP o USD");
    }

    [Fact]
    public async Task LaTrmDelDiaEsCeroEnPesosYLaOficialEnDolares()
    {
        (await _servicio.TrmDelDiaAsync("COP", Ct)).Valor.Should().Be(0m);
        (await _servicio.TrmDelDiaAsync(null, Ct)).Valor.Should().Be(0m);
        (await _servicio.TrmDelDiaAsync("USD", Ct)).Valor.Should().Be(3306.86m);
    }

    [Fact]
    public void EnMonedaConvierteUnaSalidaEnPesosYVuelveAPesos()
    {
        var enPesos = _servicio.Calcular(2_850_000m, 0.11m, "margen").Valor;

        var enDolares = _servicio.EnMoneda(enPesos, "USD", 3306.86m).Valor;
        enDolares.Precio.Should().Be(968.36m); // 3.202.247 / 3.306,86
        enDolares.PrecioCop.Should().Be(3_202_247m);

        _servicio.EnMoneda(enDolares, "COP", 0m).Valor.Should().Be(enPesos);
        _servicio.EnMoneda(enPesos, "USD", 0m).Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
    }
}
