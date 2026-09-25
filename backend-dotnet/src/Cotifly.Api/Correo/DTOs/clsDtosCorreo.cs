namespace Cotifly.Api.Correo.DTOs;

/// <summary>
/// <c>EnvioWeb</c>: <c>{para, cc, asunto, cuerpo}</c>. Las reglas (al menos un <c>para</c>, correos válidos, asunto ≤ 300,
/// cuerpo ≤ 5000) las aplica el caso de uso (<c>CORREO_INVALIDO</c> → 400 <c>ErrorWeb</c>).
/// </summary>
public sealed record clsEnvioWebRequest(IReadOnlyList<string>? Para, IReadOnlyList<string>? Cc, string? Asunto, string? Cuerpo);

/// <summary><c>RespuestaEnvio</c>: <c>{enviado: true, message_id}</c>.</summary>
public sealed record clsRespuestaEnvio(bool Enviado, string? MessageId);
