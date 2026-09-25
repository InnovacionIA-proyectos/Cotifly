namespace Cotifly.Api.Cotizaciones.DTOs;

/// <summary>
/// <c>SeguimientoWeb</c>: <c>{ganada, motivo_rechazo}</c> (snake_case por <see cref="Comun.clsJsonSnakeCaseAttribute"/>). Ambos
/// opcionales: <c>ganada = null</c> deja la cotización sin resultado, igual que el Python.
/// </summary>
public sealed record clsSeguimientoWebRequest(bool? Ganada, string? MotivoRechazo);
