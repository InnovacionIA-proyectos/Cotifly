namespace Cotifly.Aplicacion.Correo.DTOs;

/// <summary>Mensaje listo para el transporte (<c>IClsTransporteCorreo</c>).</summary>
/// <param name="Remitente">
/// Correo del comercial (header <c>From</c>, <c>Reply-To</c> y envelope <c>MAIL FROM</c>). Si es nulo, el transporte usa su
/// remitente de respaldo (<c>Correo:SmtpRelay:FromFallback</c>).
/// </param>
/// <param name="Para">Destinatarios principales.</param>
/// <param name="Cc">Copias (también van en el envelope <c>RCPT TO</c>).</param>
/// <param name="Asunto">Asunto (UTF-8).</param>
/// <param name="CuerpoTexto">Cuerpo en texto plano.</param>
/// <param name="CuerpoHtml">Mismo cuerpo en HTML corporativo (texto escapado).</param>
/// <param name="Adjunto">PDF de la cotización.</param>
public sealed record clsMensajeCorreo(
    string? Remitente,
    IReadOnlyList<string> Para,
    IReadOnlyList<string> Cc,
    string Asunto,
    string CuerpoTexto,
    string CuerpoHtml,
    clsAdjuntoCorreo Adjunto);

/// <summary>Adjunto del correo.</summary>
public sealed record clsAdjuntoCorreo(string Nombre, byte[] Contenido, string TipoMime = "application/pdf");
