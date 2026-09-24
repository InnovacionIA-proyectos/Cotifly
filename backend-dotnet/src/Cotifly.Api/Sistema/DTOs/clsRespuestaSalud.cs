namespace Cotifly.Api.Sistema.DTOs;

/// <summary>Respuesta de <c>GET /health</c> (operación <c>salud</c> del contrato #17).</summary>
/// <param name="Status">Siempre <c>ok</c>.</param>
/// <param name="Servicio">Nombre del servicio: <c>cotifly-api</c>.</param>
/// <param name="Entorno">Entorno de ASP.NET Core (<c>ASPNETCORE_ENVIRONMENT</c>).</param>
public sealed record clsRespuestaSalud(string Status, string Servicio, string Entorno);
