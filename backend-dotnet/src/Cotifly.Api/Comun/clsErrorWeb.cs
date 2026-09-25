using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Comun;

/// <summary>
/// Error de <c>/web/*</c> y <c>/api/auth/*</c> (<c>ErrorWeb</c> del contrato): <c>{"error": "texto"}</c>.
/// </summary>
public sealed record clsErrorWeb([property: JsonPropertyName("error")] string Error)
{
    public const string MensajeSinPermiso = "No tienes permiso para esta operación.";
    public const string MensajeDemasiadasPeticiones = "Demasiadas peticiones. Intenta de nuevo en unos segundos.";
    public const string MensajeCuerpoInvalido = "Cuerpo inválido.";

    /// <summary>
    /// Respuesta 400 de la validación automática de <c>[ApiController]</c> (cuerpo ausente, JSON mal formado, tipos
    /// erróneos): <c>ErrorWeb</c> con los campos afectados, en lugar del <c>ProblemDetails</c> por defecto.
    /// </summary>
    public static IActionResult RespuestaValidacion(ActionContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var campos = contexto.ModelState
            .Where(e => e.Value?.Errors.Count > 0 && !string.IsNullOrEmpty(e.Key))
            .Select(e => e.Key.TrimStart('$', '.'))
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var mensaje = campos.Count == 0 ? MensajeCuerpoInvalido : $"{MensajeCuerpoInvalido} Revisa: {string.Join(", ", campos)}.";
        return new BadRequestObjectResult(new clsErrorWeb(mensaje));
    }
}
