namespace Cotifly.Api.Administracion.DTOs;

/// <summary>
/// <c>ActualizarLineaNegocio</c>: <c>{margen, metodo_calculo, is_active}</c>, los tres obligatorios. Llegan anulables para que la
/// falta de uno responda 400 con el mensaje del caso de uso.
/// </summary>
public sealed record clsActualizarLineaNegocioRequest(decimal? Margen, string? MetodoCalculo, bool? IsActive);
