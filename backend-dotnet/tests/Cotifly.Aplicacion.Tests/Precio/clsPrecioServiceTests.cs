using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Tests.Precio;

/// <summary>
/// <see cref="clsPrecioService"/> adapta la regla pura al contrato <c>SalidaPrecio</c>. Los casos de paridad de
/// <c>tests/test_precio.py</c> están en <c>Cotifly.Dominio.Tests/Precio/clsCalculadoraPrecioTests</c>.
/// </summary>
public sealed class clsPrecioServiceTests
{
    private readonly clsPrecioService _servicio = new();

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
}
