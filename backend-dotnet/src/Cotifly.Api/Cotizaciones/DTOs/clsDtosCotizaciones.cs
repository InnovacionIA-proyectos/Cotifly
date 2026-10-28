namespace Cotifly.Api.Cotizaciones.DTOs;

/// <summary>
/// <c>SeguimientoWeb</c>: <c>{ganada, motivo_rechazo, comentario}</c> (snake_case por <see cref="Comun.clsJsonSnakeCaseAttribute"/>). Todos
/// opcionales: <c>ganada = null</c> deja la cotización sin resultado, igual que el Python. HU-018.1 (#155): con <c>ganada = false</c>,
/// <c>motivo_rechazo</c> es de la lista de <c>GET /web/motivos-perdida</c> y <c>Otro</c> exige <c>comentario</c>.
/// </summary>
public sealed record clsSeguimientoWebRequest(bool? Ganada, string? MotivoRechazo, string? Comentario = null);
