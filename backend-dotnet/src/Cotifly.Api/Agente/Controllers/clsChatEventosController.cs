using System.Text.Json;
using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Agente.Controllers;

/// <summary>
/// Webhook de Google Chat (<c>POST /chat/events</c>, port de <c>orquestador/webhook.py</c>): verifica el JWT de Google, parsea el
/// evento (formato clásico o add-on), lo encola y acusa de inmediato (&lt; 30 s) sin esperar al agente; el worker procesa el turno y
/// publica la respuesta en el hilo. No usa la autenticación de ClientePro: la identidad sale del email del evento (ADR-011).
/// </summary>
[ApiController]
[Route("chat")]
[AllowAnonymous]
public sealed class clsChatEventosController(
    IClsVerificadorJwtGoogleChat verificador,
    IClsColaEventosChat cola,
    ILogger<clsChatEventosController> logger) : ControllerBase
{
    public const string MensajeJwtInvalido = "JWT inválido o ausente";
    public const string MensajeNoJson = "payload no es JSON";

    /// <summary>
    /// 200 acuse: mensaje clásico → <c>{"text": ...}</c>; add-on → <c>hostAppDataAction.chatDataAction.createMessageAction</c>; otros
    /// eventos → <c>{}</c>. 400 si el cuerpo no es JSON; 401 si el JWT falta o es inválido.
    /// </summary>
    [HttpPost("events")]
    [RequestSizeLimit(256 * 1024)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Recibir(CancellationToken cancellationToken)
    {
        if (!await verificador.VerificarAsync(Request.Headers.Authorization.ToString(), cancellationToken))
        {
            return Unauthorized(new clsErrorWeb(MensajeJwtInvalido));
        }

        JsonDocument documento;
        try
        {
            documento = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return BadRequest(new clsErrorWeb(MensajeNoJson));
        }

        using (documento)
        {
            var evento = clsParserEventoChat.Parsear(documento.RootElement);
            if (!evento.EsMensaje)
            {
                // Eventos que no son mensajes (ADDED_TO_SPACE, etc.): acuse sin cuerpo.
                return Ok(clsParserEventoChat.Acuse(evento.EsAddOn, null));
            }

            if (!cola.Encolar(evento))
            {
                logger.LogWarning("Webhook de Google Chat: la cola está llena; el evento de {Space} no se procesa.", evento.SpaceId);
                return Ok(clsParserEventoChat.Acuse(evento.EsAddOn, clsTextosAgente.ColaLlena));
            }

            logger.LogInformation("Webhook de Google Chat: evento {Tipo} de {Space} encolado.", evento.Tipo, evento.SpaceId);
            return Ok(clsParserEventoChat.Acuse(evento.EsAddOn, clsTextosAgente.Recibo));
        }
    }
}
