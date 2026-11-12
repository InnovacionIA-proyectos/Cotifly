using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/captura_rutas.feature</c>: el peso con tara de una ruta de contenedor viaja a Nexo como el
/// peso de su línea (DU §8: con un solo camión, con sus posiciones) y se guarda en la ruta (<c>CotizacionLineasSolicitud.pesoConTaraKg</c>, script 11).
/// </summary>
[Binding]
public sealed class clsPasosPesoConTara(clsPasosCosteo costeo)
{
    [Then("Nexo recibió una línea con el peso {int} y sus posiciones")]
    public void EntoncesNexoRecibioElPesoConTara(int peso)
    {
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        var linea = alta["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["peso"]!.GetValue<decimal>().Should().Be(peso);
        linea["posiciones"]!.AsArray().Should().NotBeEmpty("#250 (DU §8): un peso mayor que la suma de las posiciones es la tara");
        costeo.Nexo.AltasPesoInconsistente.Should().Be(0);
    }

    [Then("la ruta {int} de {string} guarda el peso con tara {string} y el peso de carga {string}")]
    public async Task EntoncesGuardaElPesoConTara(int orden, string cotizacion, string conTara, string carga) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT CONCAT(CAST(l.pesoConTaraKg AS nvarchar(20)), '|', CAST(l.pesoKg AS nvarchar(20))) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + "JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}"))
            .Should().Be($"{conTara}|{carga}");
}
