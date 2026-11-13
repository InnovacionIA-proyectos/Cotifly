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
/// <param name="Cuerpo">Por defecto la plantilla del correo (máximo 5000).</param>
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
/// <c>enviado = true</c> significa que MailSender ACEPTÓ el correo (202); la entrega real se ve en <see cref="Estado"/> y en el historial
/// (<c>GET .../correos</c>).
/// </summary>
/// <param name="MessageId">Id del correo en MailSender (el <c>id</c> del 202).</param>
/// <param name="RegistradoEnBd">
/// False solo si MailSender aceptó el correo pero la base no pudo registrar el envío: no se reporta como error para que el comercial no
/// lo reenvíe (queda un error en el log).
/// </param>
public sealed record clsSalidaEnvioCorreo(bool Enviado, string MessageId, bool RegistradoEnBd)
{
    /// <summary>Id del correo en Cotifly (fila de <c>Correos</c>; el del historial y del reintento).</summary>
    public Guid CorreoId { get; init; }

    /// <summary>Estado del correo al responder (<c>encolado</c>).</summary>
    public string Estado { get; init; } = "encolado";

    /// <summary>Buzón de pruebas al que se redirigió (fuera de producción); <c>null</c> si va a los destinatarios reales.</summary>
    public string? RedirigidoA { get; init; }
}

/// <summary>Borrador del formulario de envío para la vista previa (<c>EnvioWeb</c> opcional).</summary>
public sealed record clsBorradorCorreo(
    IReadOnlyList<string>? Para = null,
    IReadOnlyList<string>? Cc = null,
    string? Asunto = null,
    string? Cuerpo = null);

/// <summary>Vista previa del correo (<c>VistaPreviaCorreo</c>): nada se envía.</summary>
/// <param name="Cuerpo">Texto del comercial (editable), sin el bloque fijo del resumen.</param>
/// <param name="Adjunto">Nombre del PDF que se adjuntará.</param>
public sealed record clsVistaPreviaCorreo(string Asunto, string Cuerpo, IReadOnlyList<string> Para, IReadOnlyList<string> Cc, string Adjunto)
{
    /// <summary>El cuerpo en HTML tal como lo recibe el cliente (texto, resumen con el total y firma; sin el aviso de pruebas).</summary>
    public string? CuerpoHtml { get; init; }

    /// <summary>Bloque fijo del resumen (precio por ruta y total, sin desglose); <c>null</c> si la cotización no tiene total.</summary>
    public Services.clsResumenCorreo? Resumen { get; init; }

    /// <summary>Copia oculta que se agrega al enviar (el correo del comercial), o <c>null</c> si no tiene correo.</summary>
    public string? CopiaOculta { get; init; }

    /// <summary>¿El ambiente redirige los correos al buzón de pruebas? (aviso "ambiente de pruebas" en el frontend).</summary>
    public bool EntornoPruebas { get; init; }

    /// <summary>Buzón de pruebas que recibirá el correo con <see cref="EntornoPruebas"/>; <c>null</c> en producción.</summary>
    public string? RedirigidoA { get; init; }

    /// <summary>Texto del aviso de pruebas para mostrar tal cual; <c>null</c> en producción.</summary>
    public string? Aviso { get; init; }

    /// <summary>
    /// Las rutas con el seguro de la mercancía marcado sin valor de la mercancía (no suma ni sale en el PDF); <c>null</c>
    /// si no hay ninguna. El mismo aviso de la vista del costeo.
    /// </summary>
    public Costeo.DTOs.clsAvisoSeguro? AvisoSeguro { get; init; }
}

/// <summary>
/// Un envío del historial de la cotización (<c>GET /web/cotizaciones/{folio}/correos</c>), con el estado que reporta
/// MailSender. Los destinatarios son los originales aunque el correo se haya redirigido al buzón de pruebas.
/// </summary>
public sealed record clsEnvioCorreoHistorial(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Estado,
    IReadOnlyList<string> Para,
    IReadOnlyList<string> Cc,
    string? Asunto,
    string? Error,
    int? Intentos,
    DateTimeOffset? EnviadoAt,
    DateTimeOffset? EstadoActualizadoAt,
    string? RedirigidoA,
    string? IdMailSender,
    bool Reintentable);

/// <summary>Destinatarios del último envío a un cliente (<c>Destinatarios</c>); listas vacías si nunca se le envió.</summary>
public sealed record clsDestinatarios(IReadOnlyList<string> Para, IReadOnlyList<string> Cc)
{
    public static readonly clsDestinatarios Vacios = new([], []);
}
