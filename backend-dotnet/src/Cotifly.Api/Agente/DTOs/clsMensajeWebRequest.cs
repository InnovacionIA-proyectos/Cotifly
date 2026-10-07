namespace Cotifly.Api.Agente.DTOs;

/// <summary>
/// <c>MensajeWeb</c> de <c>POST /web/chat</c>: <c>{mensaje, sesion_id, cotizacion_origen_id?}</c> (snake_case). Se valida en el controller
/// para responder los textos del contrato (<c>sesion_id inválido</c>). <c>cotizacion_origen_id</c> (UUID, opcional) inicia una nueva versión
/// de una cotización del comercial (auditoría de #12, I2). <c>moneda</c> (<c>COP</c> | <c>USD</c>, opcional) es el selector del formulario (HU-012.2).
/// <c>folio</c> (HU-013.6, #102) es la cotización sobre la que trabaja el asistente: obligatorio con la creación por chat deshabilitada.
/// <c>cotizacion_origen_id</c> y <c>moneda</c> quedan obsoletos (solo los usa el agente legado).
/// </summary>
public sealed record clsMensajeWebRequest(string? Mensaje, string? SesionId, string? CotizacionOrigenId = null, string? Moneda = null, string? Folio = null);
