namespace Cotifly.Aplicacion.Pdf.Interfaces;

/// <summary>
/// URL firmada de vida corta para abrir el PDF sin <c>Authorization</c> (ADR-011 §3): Google Chat no envía el token al abrir un
/// enlace. HMAC-SHA256 sobre el archivo, el comercial y el vencimiento (por defecto 15 minutos). La consola web sigue con Bearer.
/// </summary>
public interface IClsFirmaUrlPdf
{
    /// <summary>Token para <c>GET /pdfs/{archivo}?t=...</c> del comercial <paramref name="usuarioId"/>.</summary>
    string Firmar(string archivo, Guid usuarioId);

    /// <summary>Comercial del token si la firma corresponde al archivo y no venció; <c>null</c> si no (alterado, vencido, de otro archivo).</summary>
    Guid? Verificar(string archivo, string? token);
}
