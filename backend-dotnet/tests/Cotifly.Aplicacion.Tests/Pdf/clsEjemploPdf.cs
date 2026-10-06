using Cotifly.Aplicacion.Pdf.DTOs;

namespace Cotifly.Aplicacion.Tests.Pdf;

/// <summary>Payload de ejemplo del Python (<c>tests/fixtures/cotizacion/ejemplo-solicitud.json</c>), nuevo en cada llamada.</summary>
internal static class clsEjemploPdf
{
    public static readonly string Ruta = Path.Combine(AppContext.BaseDirectory, "fixtures", "cotizacion", "ejemplo-solicitud.json");

    public static clsCotizacionPdfPayload Payload() =>
        clsCotizacionPdfPayload.DesdeJson(File.ReadAllText(Ruta)).Valor;

    /// <summary>Payload multi-sección como el de <c>test_multi_seccion_usa_armar_multi</c>.</summary>
    public static clsCotizacionPdfPayload Multi()
    {
        var payload = Payload();
        payload.Tarifa = null;
        payload.Secciones =
        [
            new clsTarifaPayload { Titulo = "TRAMO 1", Origen = "YUMBO", Filas = [new clsFilaTarifaPayload { Destino = "BUN", VehiculoPeso = "Tractomula", Tarifa = 3_000_000m }] },
            new clsTarifaPayload { Titulo = "TRAMO 2", Origen = "BOGOTA", Filas = [new clsFilaTarifaPayload { Destino = "MED", VehiculoPeso = "Sencillo", Tarifa = 1_200_000m }] },
        ];
        return payload;
    }
}
