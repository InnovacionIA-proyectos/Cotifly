namespace Cotifly.Aplicacion.Correo.DTOs;

/// <summary>
/// Entrada de <c>enviar_correo</c> y de <c>POST /web/cotizaciones/{folio}/enviar</c> (<c>EnvioWeb</c>).
/// </summary>
/// <param name="UsuarioId">
/// Comercial autenticado: es la confirmación del envío (<c>confirmado_por</c>), el dueño que se exige y el remitente.
/// Sale del token, nunca del LLM.
/// </param>
/// <param name="Para">Destinatarios principales (al menos uno).</param>
/// <param name="CotizacionId">Cotización por id (herramienta del agente). Debe venir este o <paramref name="Folio"/>.</param>
/// <param name="Folio">Cotización por folio (endpoint web).</param>
/// <param name="Cc">Copias.</param>
/// <param name="Asunto">Por defecto <c>Cotización {folio} — Botero Soto</c> (máximo 300).</param>
/// <param name="Cuerpo">Por defecto la plantilla del Python (máximo 5000).</param>
/// <param name="ThreadId">Hilo de la conversación, para la auditoría.</param>
public sealed record clsEntradaEnviarCorreo(
    Guid UsuarioId,
    IReadOnlyList<string> Para,
    Guid? CotizacionId = null,
    string? Folio = null,
    IReadOnlyList<string>? Cc = null,
    string? Asunto = null,
    string? Cuerpo = null,
    string? ThreadId = null);

/// <summary>
/// Salida del envío (<c>RespuestaEnvio</c>: <c>{enviado, message_id}</c>, más <c>registrado_en_bd</c> de la herramienta).
/// </summary>
/// <param name="RegistradoEnBd">
/// False solo si el correo salió pero la base no pudo registrar el envío: no se reporta como error para que el comercial no
/// lo reenvíe (queda un error en el log).
/// </param>
public sealed record clsSalidaEnvioCorreo(bool Enviado, string MessageId, bool RegistradoEnBd);

/// <summary>Borrador del formulario de envío para la vista previa (<c>EnvioWeb</c> opcional).</summary>
public sealed record clsBorradorCorreo(
    IReadOnlyList<string>? Para = null,
    IReadOnlyList<string>? Cc = null,
    string? Asunto = null,
    string? Cuerpo = null);

/// <summary>Vista previa del correo (<c>VistaPreviaCorreo</c>): nada se envía.</summary>
/// <param name="Cuerpo">Cuerpo en texto plano, tal como sale.</param>
/// <param name="Adjunto">Nombre del PDF que se adjuntará.</param>
public sealed record clsVistaPreviaCorreo(string Asunto, string Cuerpo, IReadOnlyList<string> Para, IReadOnlyList<string> Cc, string Adjunto);

/// <summary>Destinatarios del último envío a un cliente (<c>Destinatarios</c>); listas vacías si nunca se le envió.</summary>
public sealed record clsDestinatarios(IReadOnlyList<string> Para, IReadOnlyList<string> Cc)
{
    public static readonly clsDestinatarios Vacios = new([], []);
}
