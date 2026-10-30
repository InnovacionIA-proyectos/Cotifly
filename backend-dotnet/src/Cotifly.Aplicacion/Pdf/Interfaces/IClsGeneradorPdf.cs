using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Pdf.DTOs;

namespace Cotifly.Aplicacion.Pdf.Interfaces;

/// <summary>
/// Puerto de maquetación del PDF (ADR-006). El adaptador (<c>clsGeneradorPdfMigraDoc</c>) reproduce el layout de
/// <c>armar_cotizacion.py</c> / <c>armar_multi.py</c> sin tomar decisiones de negocio: todo viene resuelto en el documento.
/// </summary>
/// <remarks>
/// PDFsharp-MigraDoc no es thread-safe (#92, #95): el adaptador renderiza un documento a la vez por proceso y los demás esperan
/// turno hasta <c>Pdf:TiempoEsperaRenderSegundos</c>. Si la espera se agota lanza <see cref="clsRenderPdfOcupadoException"/>.
/// </remarks>
public interface IClsGeneradorPdf
{
    /// <summary>
    /// Renderiza el documento cuando le llega el turno. Lanza <see cref="clsRenderPdfOcupadoException"/> si la espera se agota,
    /// <see cref="OperationCanceledException"/> si se cancela mientras espera, y otra excepción si las fuentes o recursos
    /// embebidos no se pueden cargar.
    /// </summary>
    Task<clsPdfGenerado> GenerarAsync(clsDocumentoCotizacion documento, CancellationToken cancellationToken = default);
}
