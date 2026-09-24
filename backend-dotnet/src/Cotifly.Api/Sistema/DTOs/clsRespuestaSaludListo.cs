using System.Text.Json.Serialization;

namespace Cotifly.Api.Sistema.DTOs;

/// <summary>Respuesta de <c>GET /health/ready</c> (operación <c>saludListo</c> del contrato #17).</summary>
/// <param name="Status"><c>ready</c> (200) o <c>unavailable</c> (503).</param>
/// <param name="Detalle">Solo en 503: qué dependencia no está lista. Nunca incluye cadenas de conexión.</param>
public sealed record clsRespuestaSaludListo(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detalle = null)
{
    public const string Listo = "ready";
    public const string NoDisponible = "unavailable";
}
