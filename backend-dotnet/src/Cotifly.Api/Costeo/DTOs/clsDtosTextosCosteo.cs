namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// <c>TextosCotizacionWeb</c>: cuerpo de <c>PUT /web/cotizaciones/{folio}/costeo/textos</c>. Los límites (asunto de 1 a 120
/// caracteres, introducción de 1 a 1100, sin URL ni correos) los valida Aplicación para responder <c>TEXTOS_COTIZACION_INVALIDOS</c>.
/// </summary>
public sealed record clsTextosCotizacionWebRequest(string? Asunto, string? Introduccion);

/// <summary>
/// <c>RedactarTextosWeb</c>: cuerpo OPCIONAL de <c>POST /web/cotizaciones/{folio}/costeo/textos/redactar</c>. Sin
/// cuerpo (o con <c>indicaciones_redaccion</c> en <c>null</c>), la IA redacta sin indicaciones. <c>indicaciones_redaccion</c> es una
/// PREFERENCIA de enfoque o tono para el redactor con IA (no es <c>notas</c>, la de la Mesa de Costeo): hasta
/// <see cref="Aplicacion.Redaccion.DTOs.clsContextoRedaccion.LargoMaximoIndicaciones"/> caracteres (más largo, 400
/// <c>SOLICITUD_INVALIDA</c>); vacío o solo espacios equivale a sin indicaciones. No se guarda.
/// </summary>
public sealed record clsRedactarTextosWebRequest(string? IndicacionesRedaccion);
