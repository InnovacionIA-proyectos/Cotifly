using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// El <c>scorePrioridad</c> que Nexo devuelve al crear la solicitud se guarda en la cabecera
/// (<c>CotizacionesSolicitud.scorePrioridad</c>, <c>decimal(9,4)</c>, script 09).
/// </summary>
public sealed class clsScorePrioridadTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    [Fact]
    public void AlRegistrarElEnvioGuardaElScoreDeNexo()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();

        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora, 62.5m);

        cotizacion.ScorePrioridad.Should().Be(62.5m);
    }

    [Fact]
    public void SinScoreQuedaNull()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();

        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);

        cotizacion.ScorePrioridad.Should().BeNull();
    }

    [Theory]
    [InlineData("0.73", "0.73")]
    [InlineData("0.123456", "0.1235")]
    [InlineData("-0.00005", "-0.0001")]
    [InlineData("99999.9999", "99999.9999")]
    public void SeGuardaConCuatroDecimales(string recibido, string guardado)
    {
        clsCotizacionSolicitud.NormalizarScorePrioridad(decimal.Parse(recibido, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(decimal.Parse(guardado, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("100000")]
    [InlineData("-100000")]
    [InlineData("99999.99995")]
    public void UnScoreQueNoCabeEnDecimal94SeDescartaSinFallar(string recibido)
    {
        var cotizacion = clsCotizacionMuestra.Formulario();

        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora, decimal.Parse(recibido, System.Globalization.CultureInfo.InvariantCulture));

        cotizacion.SolicitudNexoId.Should().Be("SOL-1", "el envío se registra igual: Nexo ya tiene la solicitud");
        cotizacion.ScorePrioridad.Should().BeNull();
    }

    [Fact]
    public void AlReSolicitarEnElMismoFolioSeOlvidaElScoreDeLaSolicitudAnterior()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora, 80m);

        cotizacion.PrepararReSolicitud();

        cotizacion.ScorePrioridad.Should().BeNull("el score era de la solicitud anterior de Nexo; la nueva trae el suyo");
    }
}
