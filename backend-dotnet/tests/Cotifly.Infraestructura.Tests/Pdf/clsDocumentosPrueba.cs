using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// Documentos de prueba armados con el constructor real a partir de <c>ejemplo-solicitud.json</c> (los tres casos de la
/// comparación visual de #11: nacional, internacional con bloques condicionales y multi-sección).
/// </summary>
internal static class clsDocumentosPrueba
{
    public static readonly string RutaEjemplo = Path.Combine(AppContext.BaseDirectory, "fixtures", "cotizacion", "ejemplo-solicitud.json");

    public static readonly clsFirmaDocumento Firma = new("Nathaly Vargas Garcia", "Ejecutiva Comercial", "nvargas@boterosoto.com.co", "313 415 6187");

    public static clsCotizacionPdfPayload Payload() => clsCotizacionPdfPayload.DesdeJson(File.ReadAllText(RutaEjemplo)).Valor;

    public static clsDocumentoCotizacion Documento(clsCotizacionPdfPayload payload, clsFirmaDocumento? firma = null, string folio = "PCN-19032026") =>
        clsConstructorDocumentoCotizacion.Construir(payload, folio, firma ?? Firma, new DateOnly(2026, 9, 24), "COP", 0m).Valor;

    /// <summary>Solicitud de ejemplo en USD (HU-012.3): una fila de US$996,35 con la TRM aplicada 3.306,86 y la nota de TRM mínima.</summary>
    public static clsDocumentoCotizacion Dolares()
    {
        var payload = Payload();
        payload.Tarifa!.Filas!.RemoveAt(1);
        payload.Tarifa.Filas[0].Tarifa = 996.35m;
        return clsConstructorDocumentoCotizacion.Construir(payload, "PCN-USD-1", Firma, new DateOnly(2026, 9, 27), "USD", 3306.86m).Valor;
    }

    /// <summary>Solicitud de ejemplo tal cual (Nacional, 2 filas).</summary>
    public static clsDocumentoCotizacion Nacional(clsFirmaDocumento? firma = null) => Documento(Payload(), firma);

    /// <summary>Internacional con aduana, mercancía extradimensionada y refrigerada, y servicios conexos itemizados.</summary>
    public static clsDocumentoCotizacion Internacional()
    {
        var payload = Payload();
        payload.LineaNegocio = "Internacional";
        payload.Modalidad = null;
        payload.Cliente = "ANDES CARGO S.A.S.";
        payload.Tarifa!.Titulo = "COTIZACIÓN TRANSPORTE INTERNACIONAL – BSSL";
        payload.Tarifa.Origen = "BOGOTÁ";
        payload.Tarifa.Filas = [new clsFilaTarifaPayload { Destino = "QUITO", VehiculoPeso = "Tractomula - 34 TON", Costo = 8_500_000m, Margen = 0.18m, Nota = "Incluye trámite en frontera." }];
        payload.Aduana = new clsAduanaPayload { Modalidad = "DTAI", Frontera = "Ipiales - Rumichaca", Arancel = "5%" };
        payload.Extradimensionada = new clsExtradimensionadaPayload { PesoKg = "40000", Ancho = "3,2 m", Largo = "14 m" };
        payload.Refrigerada = new clsRefrigeradaPayload { Temperatura = "-18 °C" };
        payload.ServiciosConexos =
        [
            new clsServicioConexoPayload { Descripcion = "Levante de contenedor", Precio = 350_000m },
            new clsServicioConexoPayload { Descripcion = "Devolución de contenedor", Precio = 420_000m },
        ];
        payload.DiasLibres = "3 días libres en frontera.";
        return Documento(payload, folio: "PCN-24092026");
    }

    /// <summary>Multi-sección (<c>armar_multi.py</c>) con dos tramos.</summary>
    public static clsDocumentoCotizacion Multi()
    {
        var payload = Payload();
        payload.Tarifa = null;
        payload.Secciones =
        [
            new clsTarifaPayload
            {
                Titulo = "TRAMO 1", Origen = "YUMBO",
                Filas = [new clsFilaTarifaPayload { Destino = "BUN", VehiculoPeso = "Tractomula", Tarifa = 3_000_000m, Nota = "Contenedor 40'" }],
                NotaGeneral = "Tarifa sujeta a disponibilidad de equipo.",
            },
            new clsTarifaPayload
            {
                Titulo = "TRAMO 2", Origen = "BOGOTA",
                Filas = [new clsFilaTarifaPayload { Destino = "MED", VehiculoPeso = "Sencillo", Tarifa = 1_200_000m }],
            },
        ];
        return Documento(payload, folio: "PCN-19032026-v2");
    }
}
