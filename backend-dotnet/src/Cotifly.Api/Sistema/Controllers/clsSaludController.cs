using Cotifly.Api.Sistema.DTOs;
using Cotifly.Infraestructura;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cotifly.Api.Sistema.Controllers;

/// <summary>Sondas del servicio: vida (liveness) y disponibilidad (readiness). No exigen autenticación.</summary>
[ApiController]
[Route("health")]
public sealed class clsSaludController(IHostEnvironment entorno) : ControllerBase
{
    public const string NombreServicio = "cotifly-api";

    [HttpGet]
    [ProducesResponseType<clsRespuestaSalud>(StatusCodes.Status200OK, "application/json")]
    public ActionResult<clsRespuestaSalud> Obtener() =>
        Ok(new clsRespuestaSalud("ok", NombreServicio, entorno.EnvironmentName));

    /// <summary>
    /// Readiness: ejecuta los health checks con etiqueta <c>ready</c> (la base <c>dbo_cotifly</c>).
    /// 200 <c>{"status":"ready"}</c> si todos pasan; 503 <c>{"status":"unavailable","detalle":...}</c> si no.
    /// </summary>
    [HttpGet("ready")]
    [ProducesResponseType<clsRespuestaSaludListo>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<clsRespuestaSaludListo>(StatusCodes.Status503ServiceUnavailable, "application/json")]
    public async Task<ActionResult<clsRespuestaSaludListo>> ObtenerListo(
        [FromServices] HealthCheckService salud, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(salud);

        var reporte = await salud.CheckHealthAsync(
            registro => registro.Tags.Contains(clsDependenciasInfraestructura.EtiquetaReadiness), cancellationToken);

        if (reporte.Status == HealthStatus.Healthy)
        {
            return Ok(new clsRespuestaSaludListo(clsRespuestaSaludListo.Listo));
        }

        var detalle = string.Join(
            " ",
            reporte.Entries.Values
                .Where(e => e.Status != HealthStatus.Healthy)
                .Select(e => e.Description ?? "Dependencia no disponible."));

        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new clsRespuestaSaludListo(clsRespuestaSaludListo.NoDisponible, detalle));
    }
}
