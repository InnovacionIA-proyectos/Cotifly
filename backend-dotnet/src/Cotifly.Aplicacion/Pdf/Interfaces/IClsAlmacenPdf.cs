namespace Cotifly.Aplicacion.Pdf.Interfaces;

/// <summary>
/// Puerto del almacén de PDFs. La implementación es el volumen <c>/data/pdfs</c>
/// (<c>clsAlmacenPdfVolumen</c>; ver ADR-012). Los nombres deben cumplir
/// <c>^[A-Za-z0-9_-]+\.pdf$</c> (mismo patrón que <c>GET /pdfs/{archivo}</c>): nunca rutas.
/// </summary>
public interface IClsAlmacenPdf
{
    /// <summary>
    /// Guarda un PDF nuevo y devuelve la referencia persistente (<c>Cotizaciones.pdfPath</c>). Si ya existe un archivo con
    /// ese nombre devuelve <c>null</c> y no lo toca (un folio repetido nunca reemplaza el PDF de otra cotización).
    /// </summary>
    Task<string?> GuardarNuevoAsync(string nombre, byte[] contenido, CancellationToken cancellationToken = default);

    /// <summary>Bytes del PDF por nombre, o <c>null</c> si no existe o el nombre no es válido.</summary>
    Task<byte[]?> LeerAsync(string nombre, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bytes del PDF a partir de la referencia guardada en <c>pdfPath</c>. Solo se usa el nombre del archivo, siempre
    /// dentro del almacén (una ruta manipulada en la base no permite leer otros archivos).
    /// </summary>
    Task<byte[]?> LeerPorReferenciaAsync(string? pdfPath, CancellationToken cancellationToken = default);

    /// <summary>Borra el PDF si existe (compensación cuando la cotización no se pudo registrar).</summary>
    Task EliminarAsync(string nombre, CancellationToken cancellationToken = default);
}
