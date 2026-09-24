using Cotifly.Api.Sistema.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Sistema.Controllers;

/// <summary>Sonda de vida del servicio (liveness). No exige autenticación.</summary>
[ApiController]
[Route("health")]
public sealed class clsSaludController(IHostEnvironment entorno) : ControllerBase
{
    public const string NombreServicio = "cotifly-api";

    [HttpGet]
    [ProducesResponseType<clsRespuestaSalud>(StatusCodes.Status200OK, "application/json")]
    public ActionResult<clsRespuestaSalud> Obtener() =>
        Ok(new clsRespuestaSalud("ok", NombreServicio, entorno.EnvironmentName));
}
