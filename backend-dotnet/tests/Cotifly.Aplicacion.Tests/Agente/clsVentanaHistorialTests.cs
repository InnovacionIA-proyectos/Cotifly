using Cotifly.Aplicacion.Agente.Services;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>Ventana deslizante del historial (port de las pruebas de ventana y <c>test_estimar_tokens</c> de <c>tests/test_sesiones.py</c>).</summary>
public sealed class clsVentanaHistorialTests
{
    private static List<clsTurnoTexto> Turnos(params string[] contenidos) => [.. contenidos.Select(c => new clsTurnoTexto("user", c))];

    // test_historial_completo_si_cabe
    [Fact]
    public void SiCabeSeEntregaCompleto()
    {
        var turnos = Turnos("mensaje 0", "mensaje 1", "mensaje 2", "mensaje 3", "mensaje 4");

        clsVentanaHistorial.Aplicar(turnos, 10_000).Select(t => t.Contenido).Should().Equal(turnos.Select(t => t.Contenido));
    }

    // test_historial_trunca_conservando_primero
    [Fact]
    public void TruncaConservandoElPrimeroYLosMasRecientes()
    {
        var ventana = clsVentanaHistorial.Aplicar(Turnos("ORIGINAL", "m1", "m2", "m3", "m4"), 3, _ => 1).Select(t => t.Contenido).ToList();

        ventana.Should().Equal("ORIGINAL", "m3", "m4");
    }

    // test_historial_conserva_primero_aunque_exceda. Diferencia deliberada: el último turno (el mensaje actual del comercial)
    // también se conserva; el Python podía descartarlo.
    [Fact]
    public void ConservaElPrimeroYElUltimoAunqueExcedan()
    {
        var ventana = clsVentanaHistorial.Aplicar(Turnos("solicitud original", "respuesta", "mensaje actual"), 1, _ => 5).Select(t => t.Contenido);

        ventana.Should().Equal("solicitud original", "mensaje actual");
    }

    // test_historial_vacio
    [Fact]
    public void UnHistorialVacioQuedaVacio() => clsVentanaHistorial.Aplicar([], 100).Should().BeEmpty();

    // test_estimar_tokens
    [Fact]
    public void EstimaUnTokenCadaCuatroCaracteres()
    {
        clsVentanaHistorial.EstimarTokens(null).Should().Be(0);
        clsVentanaHistorial.EstimarTokens(string.Empty).Should().Be(0);
        clsVentanaHistorial.EstimarTokens("abcd").Should().Be(1);
        clsVentanaHistorial.EstimarTokens(new string('a', 40)).Should().Be(10);
        clsVentanaHistorial.EstimarTokens("ab").Should().Be(1);
    }

    [Theory]
    [InlineData("RateLimitError: quota", true)]
    [InlineData("HTTP 429 Too Many Requests", true)]
    [InlineData("connection refused", false)]
    public void ReconoceElLimiteDePeticionesDelProveedor(string mensaje, bool esperado) =>
        clsTextosAgente.EsLimiteDePeticiones(new InvalidOperationException(mensaje)).Should().Be(esperado);

    [Fact]
    public void ReconoceElLimitePorElCodigoHttpDeUnaExcepcionInterna() =>
        clsTextosAgente.ParaFallo(new InvalidOperationException("envoltura", new HttpRequestException("x", null, System.Net.HttpStatusCode.TooManyRequests)))
            .Should().Be(clsTextosAgente.LimiteModelo);
}
