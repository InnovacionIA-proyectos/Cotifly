namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Evento de Google Chat normalizado (port de <c>EventoChat</c> de <c>orquestador/gchat.py</c>). Además del mensaje de texto,
/// se reconocen los clics en los botones de la tarjeta de previsualización (<see cref="Accion"/>).
/// </summary>
/// <param name="Tipo"><c>MESSAGE</c> | <c>CARD_CLICKED</c> | <c>ADDED_TO_SPACE</c> | <c>REMOVED_FROM_SPACE</c> | ...</param>
/// <param name="EsAddOn">True si llegó en el formato de add-on de Workspace (<c>chat.messagePayload</c>): cambia la forma del acuse.</param>
/// <param name="Accion">Función del botón pulsado (<c>confirmar_cotizacion</c>, <c>ajustar_margen</c>), si es un clic.</param>
public sealed record clsEventoChat(
    string Tipo,
    string? SpaceId = null,
    string? ThreadId = null,
    string? Texto = null,
    string? UsuarioEmail = null,
    string? UsuarioNombre = null,
    bool EsAddOn = false,
    string? Accion = null)
{
    public const string AccionConfirmar = "confirmar_cotizacion";
    public const string AccionAjustarMargen = "ajustar_margen";

    /// <summary>Texto que se le pasa al agente por el botón "Confirmar y generar PDF".</summary>
    public const string TextoConfirmar = "Confirmo";

    /// <summary>Texto que se le pasa al agente por el botón "Ajustar margen".</summary>
    public const string TextoAjustarMargen = "Quiero ajustar el margen.";

    /// <summary>True si dispara un turno del agente: un mensaje con texto o el clic en un botón conocido.</summary>
    public bool EsMensaje => TextoParaAgente is not null;

    /// <summary>Texto del turno: el del mensaje, o el que corresponde al botón pulsado.</summary>
    public string? TextoParaAgente => Tipo switch
    {
        "MESSAGE" when !string.IsNullOrWhiteSpace(Texto) => Texto.Trim(),
        "CARD_CLICKED" when Accion == AccionConfirmar => TextoConfirmar,
        "CARD_CLICKED" when Accion == AccionAjustarMargen => TextoAjustarMargen,
        _ => null,
    };
}
