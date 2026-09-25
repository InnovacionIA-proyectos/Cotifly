namespace Cotifly.Api.Agente.DTOs;

/// <summary>
/// <c>MensajeWeb</c> de <c>POST /web/chat</c>: <c>{mensaje, sesion_id}</c> (snake_case). Se valida en el controller para responder
/// los textos del contrato (<c>sesion_id inválido</c>).
/// </summary>
public sealed record clsMensajeWebRequest(string? Mensaje, string? SesionId);
