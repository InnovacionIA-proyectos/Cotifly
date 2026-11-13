namespace Cotifly.Api.Agente.DTOs;

/// <summary>
/// <c>MensajeWeb</c> de <c>POST /web/chat</c>: <c>{mensaje, sesion_id, cotizacion_origen_id?}</c> (snake_case). Se valida en el controller
/// para responder los textos del contrato (<c>sesion_id inválido</c>). <c>folio</c> es la cotización sobre la que trabaja el
/// asistente: obligatorio (sin él, 400 <c>FOLIO_REQUERIDO</c>). <c>cotizacion_origen_id</c> (UUID) y <c>moneda</c> (<c>COP</c> |
/// <c>USD</c>) están obsoletos: se aceptan y se ignoran por compatibilidad del contrato.
/// </summary>
public sealed record clsMensajeWebRequest(string? Mensaje, string? SesionId, string? CotizacionOrigenId = null, string? Moneda = null, string? Folio = null);
