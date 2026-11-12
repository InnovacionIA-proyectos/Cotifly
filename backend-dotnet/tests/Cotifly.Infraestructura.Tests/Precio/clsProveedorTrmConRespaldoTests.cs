using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Precio;

/// <summary><see cref="clsProveedorTrmConRespaldo"/> (ADR-014): DolarApi primero y la Superintendencia solo si la principal falla.</summary>
public sealed class clsProveedorTrmConRespaldoTests
{
    private static readonly DateOnly _fecha = new(2026, 10, 5);
    private static readonly clsTrm _deDolarApi = new(3273.49m, _fecha, _fecha, "dolarapi");
    private static readonly clsTrm _deSuperfinanciera = new(3280.10m, _fecha, _fecha, "superfinanciera");

    private readonly IClsProveedorTrm _principal = Substitute.For<IClsProveedorTrm>();
    private readonly IClsProveedorTrm _respaldo = Substitute.For<IClsProveedorTrm>();
    private readonly clsLoggerFalso<clsProveedorTrmConRespaldo> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConLaPrincipalBienNoConsultaElRespaldo()
    {
        _principal.ObtenerAsync(_fecha, Arg.Any<CancellationToken>()).Returns(_deDolarApi);

        var resultado = await Cadena().ObtenerAsync(_fecha, Ct);

        resultado.Valor.Should().Be(_deDolarApi);
        await _respaldo.DidNotReceive().ObtenerAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        _logger.Registros.Should().BeEmpty();
    }

    [Fact]
    public async Task SiLaPrincipalFallaUsaElRespaldoYDejaUnWarningConLaFuente()
    {
        _principal.ObtenerAsync(_fecha, Arg.Any<CancellationToken>()).Returns(NoDisponible("HTTP 503 (ServiceUnavailable)"));
        _respaldo.ObtenerAsync(_fecha, Arg.Any<CancellationToken>()).Returns(_deSuperfinanciera);

        var resultado = await Cadena().ObtenerAsync(_fecha, Ct);

        resultado.Valor.Should().Be(_deSuperfinanciera);
        resultado.Valor.Fuente.Should().Be("superfinanciera");
        _logger.Registros.Should().ContainSingle().Which.Should().Match<clsRegistroLog>(r =>
            r.Nivel == LogLevel.Warning && r.Mensaje.Contains("respaldo") && r.Mensaje.Contains("superfinanciera") && r.Mensaje.Contains("HTTP 503"));
    }

    [Fact]
    public async Task SiAmbasFallanEsTrmNoDisponibleConLosDosMotivos()
    {
        _principal.ObtenerAsync(_fecha, Arg.Any<CancellationToken>()).Returns(NoDisponible("tiempo agotado"));
        _respaldo.ObtenerAsync(_fecha, Arg.Any<CancellationToken>()).Returns(NoDisponible("HTTP 500 (InternalServerError)"));

        var resultado = await Cadena().ObtenerAsync(_fecha, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Mensaje.Should().Contain("TRM").And.Contain("pesos");
        resultado.Error.Detalle.Should().Be("principal: tiempo agotado; respaldo: HTTP 500 (InternalServerError)");
        _logger.Registros.Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task PasaLaFechaYElTokenAAmbasFuentes()
    {
        using var cancelacion = new CancellationTokenSource();
        _principal.ObtenerAsync(_fecha, cancelacion.Token).Returns(NoDisponible("error de red"));
        _respaldo.ObtenerAsync(_fecha, cancelacion.Token).Returns(_deSuperfinanciera);

        (await Cadena().ObtenerAsync(_fecha, cancelacion.Token)).EsExitoso.Should().BeTrue();

        await _principal.Received(1).ObtenerAsync(_fecha, cancelacion.Token);
        await _respaldo.Received(1).ObtenerAsync(_fecha, cancelacion.Token);
    }

    private clsProveedorTrmConRespaldo Cadena() => new(_principal, _respaldo, _logger);

    private static clsResultado<clsTrm> NoDisponible(string motivo) =>
        new clsErrorDominio(clsCodigosError.TrmNoDisponible, "No se pudo obtener la TRM; cotiza en pesos.", motivo);
}
