using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Opciones del PDF que consume el caso de uso (sección <c>Pdf</c>; el almacén lee la misma sección en
/// <c>clsOpcionesAlmacenPdf</c> de Infraestructura). Port de <c>pdf_base_url</c> de <c>shared/config.py</c>.
/// </summary>
public sealed class clsOpcionesPdf
{
    public const string Seccion = "Pdf";

    /// <summary>
    /// Base pública de <c>pdf_url</c> (<c>{BaseUrl}/pdfs/{folio}.pdf</c>). Vacía (por defecto) deja la URL relativa
    /// (<c>/pdfs/PCN-...pdf</c>), que el frontend resuelve contra la API (<c>GET /pdfs/{archivo}</c> exige sesión).
    /// </summary>
    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Segundos que un PDF espera su turno de render (<c>Pdf__TiempoEsperaRenderSegundos</c>, por defecto 30). El render es
    /// uno a la vez por proceso (PDFsharp-MigraDoc no es thread-safe, #92/#95); si la espera se agota responde
    /// <c>PDF_RENDER_NO_DISPONIBLE</c> (503).
    /// </summary>
    [Range(1, 600)]
    public int TiempoEsperaRenderSegundos { get; set; } = 30;

    /// <summary>
    /// <c>Pdf__PermitirCotizacionChat</c> (por defecto <c>false</c>, HU-021.6): habilita <c>clsGenerarPdfService</c>, el camino legado que
    /// crea una cotización "del chat" sin municipios (se salta la decisión 2 de HU-021). Desde ADR-018 la única creación es el formulario
    /// (<c>POST /web/solicitudes-costeo</c>) y el asistente solo propone; apagado responde <c>COTIZACION_CHAT_RETIRADA</c>. Solo las
    /// pruebas del render del PDF legado lo encienden.
    /// </summary>
    public bool PermitirCotizacionChat { get; set; }
}
