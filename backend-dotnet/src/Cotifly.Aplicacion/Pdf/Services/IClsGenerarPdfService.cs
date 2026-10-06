using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Caso de uso <c>generar_pdf</c>: valida el payload, arma y renderiza el PDF, lo guarda en el almacén y registra la
/// cotización. Errores: <c>PDF_PAYLOAD_INVALIDO</c>, <c>MARGEN_LINEA_DESCONOCIDA</c>, <c>FOLIO_DUPLICADO</c>,
/// <c>PDF_RENDER_NO_DISPONIBLE</c> y <c>ERROR_INTERNO</c> (comercial inexistente).
/// </summary>
public interface IClsGenerarPdfService
{
    Task<clsResultado<clsSalidaPdf>> GenerarAsync(clsEntradaGenerarPdf entrada, CancellationToken cancellationToken = default);
}
