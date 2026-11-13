using Cotifly.Dominio.Correo;

namespace Cotifly.Aplicacion.Correo.DTOs;

/// <summary>Mensaje listo para el transporte (<c>IClsTransporteCorreo</c>).</summary>
/// <param name="Remitente">
/// Correo del comercial (header <c>From</c>; MailSender no admite <c>Reply-To</c>, así que las respuestas del cliente le llegan a él). Si es
/// nulo, el transporte usa su remitente por defecto (<c>Correo:MailSender:Remitente</c>).
/// </param>
/// <param name="Para">Destinatarios principales.</param>
/// <param name="Cc">Copias.</param>
/// <param name="Asunto">Asunto (UTF-8).</param>
/// <param name="CuerpoTexto">Cuerpo en texto plano (lo que escribió el comercial).</param>
/// <param name="CuerpoHtml">Cuerpo en HTML corporativo: el texto escapado, el resumen con el total y la firma. Es el que viaja (<c>IsHtml</c>).</param>
/// <param name="Adjunto">PDF de la cotización.</param>
public sealed record clsMensajeCorreo(
    string? Remitente,
    IReadOnlyList<string> Para,
    IReadOnlyList<string> Cc,
    string Asunto,
    string CuerpoTexto,
    string CuerpoHtml,
    clsAdjuntoCorreo Adjunto)
{
    /// <summary>Copias ocultas (el comercial dueño).</summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary><c>X-Correlation-ID</c> de MailSender: <c>{folio}:{id del correo en Cotifly}</c>.</summary>
    public string? IdCorrelacion { get; init; }

    /// <summary><c>Metadata</c> de MailSender (JSON): origen, folio, id del correo y de la cotización.</summary>
    public IReadOnlyDictionary<string, string?> Metadatos { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Adjunto del correo.</summary>
public sealed record clsAdjuntoCorreo(string Nombre, byte[] Contenido, string TipoMime = "application/pdf");

/// <summary>
/// Lo que devuelve el transporte cuando acepta el mensaje. Con MailSender: <see cref="IdExterno"/> (el <c>id</c> del 202) y
/// <see cref="EstadoCorreo.Encolado"/>; la entrega real se consulta después (<c>IClsSeguimientoCorreoExterno</c>).
/// </summary>
public sealed record clsEnvioAceptado(Guid? IdExterno, EstadoCorreo Estado, string? MessageId);

/// <summary>Estado de un correo en MailSender, ya traducido al de Cotifly.</summary>
/// <param name="Estado">Estado de Cotifly (<c>encolado</c>, <c>enviando</c>, <c>enviado</c> o <c>fallido</c>).</param>
/// <param name="Error"><c>lastError</c> de MailSender (o el motivo de un estado final sin error).</param>
/// <param name="Intentos"><c>attemptCount</c>.</param>
/// <param name="EnviadoAt"><c>sentAt</c>.</param>
public sealed record clsEstadoEntregaCorreo(EstadoCorreo Estado, string? Error, int? Intentos, DateTimeOffset? EnviadoAt);
