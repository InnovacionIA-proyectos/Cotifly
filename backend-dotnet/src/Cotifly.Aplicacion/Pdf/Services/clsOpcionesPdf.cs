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
}
