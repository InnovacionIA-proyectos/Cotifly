namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// <c>TextosCotizacionWeb</c> (HU-013.5): cuerpo de <c>PUT /web/cotizaciones/{folio}/costeo/textos</c>. Los límites (asunto de 1 a 120
/// caracteres, introducción de 1 a 900, sin URL ni correos) los valida Aplicación para responder <c>TEXTOS_COTIZACION_INVALIDOS</c>.
/// </summary>
public sealed record clsTextosCotizacionWebRequest(string? Asunto, string? Introduccion);
