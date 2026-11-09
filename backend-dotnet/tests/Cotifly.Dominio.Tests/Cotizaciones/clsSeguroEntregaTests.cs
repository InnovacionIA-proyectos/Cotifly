using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9): una ruta con el seguro marcado pero sin valor de la mercancía (la edición quitó el valor: el check queda sin
/// tasa ni costo) no deja generar el PDF ni enviar la cotización, para no cobrar de menos. El error lista las rutas.
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
    public void SinSeguroOConSeguroCalculadoNoHayNadaQueBloquear()
    {
        var sinSeguro = Costeada();
        var conSeguro = Costeada();
        conSeguro.CambiarSeguro(1, true, Tasa, _ahora);

        sinSeguro.RutasConSeguroSinValor().Should().BeEmpty();
        conSeguro.RutasConSeguroSinValor().Should().BeEmpty();
        conSeguro.ValidarSeguroAntesDe("generar el PDF").Should().BeNull();
    }

    [Fact]
    public void ElSeguroMarcadoSinValorDeLaMercanciaBloqueaConElCodigoYLasRutas()
    {
        var cotizacion = ConSeguroSinValor();

        var error = cotizacion.ValidarSeguroAntesDe("generar el PDF");

        cotizacion.RutasConSeguroSinValor().Select(r => r.Orden).Should().Equal(1);
        error!.Codigo.Should().Be(clsCodigosError.SeguroSinValorMercancia);
        error.Mensaje.Should().Contain("generar el PDF").And.Contain("ruta 1").And.Contain(cotizacion.Folio);
        var rutas = error.Datos!["rutas"].Should().BeAssignableTo<IReadOnlyList<IReadOnlyDictionary<string, object?>>>().Subject;
        rutas.Should().ContainSingle();
        rutas[0]["linea_orden"].Should().Be(1);
        rutas[0]["linea_solicitud_id"].Should().Be(cotizacion.PrimeraRuta!.Id);
    }
}
