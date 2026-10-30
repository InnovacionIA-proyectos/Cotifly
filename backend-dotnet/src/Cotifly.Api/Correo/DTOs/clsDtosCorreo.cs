namespace Cotifly.Api.Correo.DTOs;

/// <summary>
/// <c>EnvioWeb</c>: <c>{para, cc, asunto, cuerpo}</c>. Las reglas (al menos un <c>para</c>, correos válidos, asunto ≤ 300,
/// cuerpo ≤ 5000) las aplica el caso de uso (<c>CORREO_INVALIDO</c> → 400 <c>ErrorWeb</c>).
/// </summary>
public sealed record clsEnvioWebRequest(IReadOnlyList<string>? Para, IReadOnlyList<string>? Cc, string? Asunto, string? Cuerpo);

/// <summary>
/// <c>RespuestaEnvio</c>: <c>{enviado: true, message_id}</c>. HU-033 (#199): <c>enviado</c> = MailSender aceptó el correo (202) y
/// <c>message_id</c> es su id; además <c>correo_id</c> (el envío en Cotifly, para el historial y el reintento), <c>estado</c>
/// (<c>encolado</c>) y <c>redirigido_a</c> (buzón de pruebas fuera de producción, o <c>null</c>).
/// </summary>
public sealed record clsRespuestaEnvio(bool Enviado, string? MessageId)
{
    public Guid? CorreoId { get; init; }

    public string? Estado { get; init; }

    public string? RedirigidoA { get; init; }
}

/// <summary>HU-033 (#199): <c>HistorialCorreos</c>: <c>{folio, correos}</c>, del más reciente al más antiguo.</summary>
public sealed record clsHistorialCorreosRespuesta(string Folio, IReadOnlyList<Cotifly.Aplicacion.Correo.DTOs.clsEnvioCorreoHistorial> Correos);
