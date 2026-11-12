using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// ADR-032 §9: las rutas con el seguro marcado pero sin valor de la mercancía (la edición quitó el valor: el check queda
/// sin tasa ni costo) se avisan, no se bloquean.
/// </summary>
public sealed class clsSeguroEntregaTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;
    private const decimal Tasa = 0.003450m;

    private static clsCotizacionSolicitud Costeada(decimal? valor = 200_000_000m) =>
        clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor });

    private static clsCotizacionSolicitud ConSeguroSinValor()
    {
        var cotizacion = Costeada();
        cotizacion.CambiarSeguro(1, true, Tasa, _ahora).Valor.Should().BeTrue();
        var ruta = cotizacion.PrimeraRuta!;
        var linea = clsSolicitudCosteoMuestra.RutaCaptura() with { ValorMercancia = null };
        var capturada = new clsRutaCapturada(linea, 3, 0, [new clsCamionPlaneado(null, null, linea.PesoKg)], []);
        cotizacion.AplicarEdicion(
            new clsEdicionSolicitud(
                new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline },
                [new clsRutaEditada(ruta.Id, capturada, Replanificada: false)]) { TasaSeguroVigente = Tasa },
            _ahora);
        ruta.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.CostoSeguro == null);
        return cotizacion;
    }

    [Fact]
    public void SinSeguroOConSeguroCalculadoNoHayRutasQueAvisar()
    {
        var sinSeguro = Costeada();
        var conSeguro = Costeada();
        conSeguro.CambiarSeguro(1, true, Tasa, _ahora);

        sinSeguro.RutasConSeguroSinValor().Should().BeEmpty();
        conSeguro.RutasConSeguroSinValor().Should().BeEmpty();
    }

    [Fact]
    public void ElSeguroMarcadoSinValorDeLaMercanciaApareceEnLasRutasQueAvisarYNoSumaAlPrecio()
    {
        var cotizacion = ConSeguroSinValor();

        cotizacion.RutasConSeguroSinValor().Should().ContainSingle().Which.Should().BeSameAs(cotizacion.PrimeraRuta);
        cotizacion.PrimeraRuta!.ValorSeguro(clsCotizacionSolicitud.MonedaPesos, 0m, out var valor).Should().BeNull();
        valor.Should().BeNull("sin valor de la mercancía el seguro no se calcula ni se suma");
    }
}
