namespace Cotifly.Api.Agente.DTOs;

/// <summary>
/// <c>MensajeWeb</c> de <c>POST /web/chat</c>: <c>{mensaje, sesion_id, cotizacion_origen_id?}</c> (snake_case). Se valida en el controller
/// para responder los textos del contrato (<c>sesion_id inválido</c>). <c>cotizacion_origen_id</c> (UUID, opcional) inicia una nueva versión
/// de una cotización del comercial (auditoría de #12, I2).
/// </summary>
public sealed record clsMensajeWebRequest(string? Mensaje, string? SesionId, string? CotizacionOrigenId = null);
