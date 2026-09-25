using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Decorador de auditoría de <see cref="IClsGenerarPdfService"/>: cada generación (exitosa, rechazada o con excepción) queda
/// en <c>EventosHerramienta</c> como <c>generar_pdf</c>, con la entrada, la salida o el error, la duración y el hilo.
/// </summary>
public sealed class clsGenerarPdfServiceAuditado(IClsGenerarPdfService interno, IClsAuditoriaService auditoria) : IClsGenerarPdfService
{
    public Task<clsResultado<clsSalidaPdf>> GenerarAsync(clsEntradaGenerarPdf entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return auditoria.AuditarAsync(
            clsAuditoriaService.HerramientaGenerarPdf,
            entrada,
            entrada.ThreadId,
            () => interno.GenerarAsync(entrada, cancellationToken));
    }
}
