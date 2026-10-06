using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.DTOs;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Normaliza el payload de Google Chat (port de <c>parsear_evento</c> de <c>orquestador/gchat.py</c>, tolerante a campos
/// ausentes) y arma el acuse síncrono (port de <c>_ack</c> de <c>orquestador/webhook.py</c>). Soporta los dos formatos:
/// <list type="bullet">
/// <item><b>Clásico</b>: <c>{type, message:{text, sender, thread, space}, space, user}</c>; el clic en una tarjeta trae
/// <c>action.actionMethodName</c>.</item>
/// <item><b>Add-on de Workspace (HTTP nuevo)</b>: <c>{chat:{messagePayload:{message, space, user}}}</c>; el clic trae
/// <c>commonEventObject.invokedFunction</c> y <c>chat.buttonClickedPayload</c>.</item>
/// </list>
/// </summary>
public static class clsParserEventoChat
{
    public static clsEventoChat Parsear(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return new clsEventoChat(string.Empty);
        }

        var chat = Objeto(payload, "chat");
        var mensajeAddOn = Objeto(chat, "messagePayload");
        var clicAddOn = Objeto(chat, "buttonClickedPayload");
        var contenedor = mensajeAddOn ?? clicAddOn;
        var esAddOn = chat is not null;

        var mensaje = Objeto(payload, "message") ?? Objeto(contenedor, "message");
        var espacio = Objeto(payload, "space") ?? Objeto(contenedor, "space") ?? Objeto(mensaje, "space");
        var hilo = Objeto(mensaje, "thread");
        var remitente = Objeto(mensaje, "sender");
        var usuario = Objeto(payload, "user") ?? Objeto(contenedor, "user") ?? Objeto(chat, "user");
        var texto = Texto(mensaje, "text");

        var accion = Texto(Objeto(payload, "action"), "actionMethodName")
                     ?? Texto(Objeto(payload, "commonEventObject"), "invokedFunction");

        var tipo = Texto(payload, "type")
                   ?? (clicAddOn is not null || (accion is not null && mensajeAddOn is null) ? "CARD_CLICKED"
                       : mensajeAddOn is not null || texto is not null ? "MESSAGE"
                       : string.Empty);

        return new clsEventoChat(
            tipo,
            Texto(espacio, "name"),
            Texto(hilo, "name"),
            texto,
            Texto(remitente, "email") ?? Texto(usuario, "email"),
            Texto(remitente, "displayName") ?? Texto(usuario, "displayName"),
            esAddOn,
            accion);
    }

    /// <summary>
    /// Acuse en el formato que espera cada modelo de app: add-on → <c>hostAppDataAction.chatDataAction.createMessageAction</c>;
    /// clásica → <c>{"text": ...}</c>; sin texto → <c>{}</c> (acuse silencioso).
    /// </summary>
    public static JsonObject Acuse(bool esAddOn, string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return [];
        }

        if (esAddOn)
        {
            return new JsonObject
            {
                ["hostAppDataAction"] = new JsonObject
                {
                    ["chatDataAction"] = new JsonObject
                    {
                        ["createMessageAction"] = new JsonObject { ["message"] = new JsonObject { ["text"] = texto } },
                    },
                },
            };
        }

        return new JsonObject { ["text"] = texto };
    }

    private static JsonElement? Objeto(JsonElement? padre, string nombre) =>
        padre is { ValueKind: JsonValueKind.Object } objeto && objeto.TryGetProperty(nombre, out var hijo) && hijo.ValueKind == JsonValueKind.Object
            ? hijo
            : null;

    private static string? Texto(JsonElement? padre, string nombre) =>
        padre is { ValueKind: JsonValueKind.Object } objeto && objeto.TryGetProperty(nombre, out var valor) && valor.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(valor.GetString())
            ? valor.GetString()
            : null;
}
