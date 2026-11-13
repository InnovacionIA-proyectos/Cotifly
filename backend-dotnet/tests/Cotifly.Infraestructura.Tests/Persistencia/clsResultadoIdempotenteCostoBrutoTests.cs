using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// La repetición con la misma <c>Idempotency-Key</c> de <c>POST .../costeo/servicios-conexos</c> devuelve el
/// <c>CosteoCotizacion</c> guardado en <c>dbo_cotifly.CapturasIdempotentes</c> (<see cref="clsResultadoIdempotenteJson"/>). El costo bruto
/// de cada ruta (<see cref="clsRutaCosteo.CostoFinal"/>, propiedad <c>init</c>) vuelve igual, también en <c>null</c>: la repetición del dueño
/// no lo pierde ni lo inventa. El ámbito de la clave es <c>"conexos:{usuarioId}"</c>, así que nunca se reenvía a otro usuario.
/// </summary>
public sealed class clsResultadoIdempotenteCostoBrutoTests
{
    [Fact]
    public void ElCostoBrutoDeCadaRutaSobreviveALaRepeticionIdempotente()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_234_567.89m, 2_000_000m], moneda: "USD", trm: 4_000m);
        var vista = clsCosteoCotizacionService.Vista(cotizacion, null);

        var leida = clsResultadoIdempotenteJson.Deserializar<clsCosteoCotizacion>(
            clsResultadoIdempotenteJson.Serializar(clsResultado<clsCosteoCotizacion>.Exito(vista)));

        leida.Valor.Rutas.Select(r => r.CostoFinal).Should().Equal(1_234_567.89m, 2_000_000m);
        leida.Valor.Rutas.Select(r => r.PrecioFinalLinea).Should().Equal(vista.Rutas.Select(r => r.PrecioFinalLinea));
    }

    [Fact]
    public void UnaRutaSinCostoBrutoVuelveSinCostoBruto()
    {
        var vista = clsCosteoCotizacionService.Vista(clsCotizacionMuestra.Costeada(flete: null), null);

        var leida = clsResultadoIdempotenteJson.Deserializar<clsCosteoCotizacion>(
            clsResultadoIdempotenteJson.Serializar(clsResultado<clsCosteoCotizacion>.Exito(vista)));

        leida.Valor.Rutas.Should().ContainSingle().Which.CostoFinal.Should().BeNull();
    }
}
