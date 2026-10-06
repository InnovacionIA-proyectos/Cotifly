using Cotifly.Aplicacion.Pdf.DTOs;

namespace Cotifly.Aplicacion.Pdf.Interfaces;

/// <summary>
/// Puerto de maquetación del PDF (ADR-006). El adaptador (<c>clsGeneradorPdfMigraDoc</c>) reproduce el layout de
/// <c>armar_cotizacion.py</c> / <c>armar_multi.py</c> sin tomar decisiones de negocio: todo viene resuelto en el documento.
/// </summary>
public interface IClsGeneradorPdf
{
    /// <summary>Renderiza el documento. Lanza si las fuentes o recursos embebidos no se pueden cargar.</summary>
    clsPdfGenerado Generar(clsDocumentoCotizacion documento);
}
