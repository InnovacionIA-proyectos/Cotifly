using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.AI;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Validador por defecto hasta #16 (HU-003.10): texto libre, sin esquema; solo recorta espacios. Se registra con <c>TryAdd</c>
/// para que #16 lo reemplace sin tocar el runner.
/// </summary>
public sealed class clsValidadorRespuestaPasante : IClsValidadorRespuestaAgente
{
    public ChatResponseFormat? FormatoRespuesta => null;

    public Task<clsResultado<string>> ValidarAsync(string? respuesta, CancellationToken cancellationToken = default) =>
        Task.FromResult(clsResultado<string>.Exito((respuesta ?? string.Empty).Trim()));
}
