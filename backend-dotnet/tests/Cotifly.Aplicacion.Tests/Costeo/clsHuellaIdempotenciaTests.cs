using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// #191 (deuda del PR #195): la huella de la <c>Idempotency-Key</c> compara los números por valor: un reintento que el cliente vuelve a
/// serializar con otra escala de decimales es la misma petición; otro valor, otra petición.
/// </summary>
public sealed class clsHuellaIdempotenciaTests
{
    private sealed record clsCuerpo(string Folio, decimal Peso, IReadOnlyList<decimal> Medidas, double? Factor);

    [Fact]
    public void LaMismaCantidadConOtraEscalaDaLaMismaHuella()
    {
        var a = clsHuellaIdempotencia.Calcular(new clsCuerpo("F-1", 1424.95m, [1.5m, 2m], 0.5));
        var b = clsHuellaIdempotencia.Calcular(new clsCuerpo("F-1", 1424.950m, [1.50m, 2.000m], 0.50));

        b.Should().Be(a);
        a.Should().HaveLength(64).And.MatchRegex("^[0-9A-F]{64}$");
    }

    [Theory]
    [InlineData("F-2", 1424.95, 1.5)]
    [InlineData("F-1", 1424.96, 1.5)]
    [InlineData("F-1", 1424.95, 15)]
    public void OtroValorDaOtraHuella(string folio, double peso, double medida)
    {
        var original = clsHuellaIdempotencia.Calcular(new clsCuerpo("F-1", 1424.95m, [1.5m], null));

        clsHuellaIdempotencia.Calcular(new clsCuerpo(folio, (decimal)peso, [(decimal)medida], null)).Should().NotBe(original);
    }

    [Fact]
    public void LaHuellaDeLaCapturaUsaLaComparacionPorValor()
    {
        var ruta = clsSolicitudCosteoMuestra.RutaCaptura() with { PesoKg = 28_500.5m };
        var entrada = new clsEntradaCaptura("900555111", "Longyin", "Importación", clsSolicitudCosteoMuestra.CrearCaptura().Encabezado,
            [new clsLineaLicitacionEntrada(null, ruta)]);
        var otraEscala = entrada with { Rutas = [new clsLineaLicitacionEntrada(null, ruta with { PesoKg = 28_500.500m })] };

        clsEnvioCosteoService.Huella(otraEscala).Should().Be(clsEnvioCosteoService.Huella(entrada));
    }
}
