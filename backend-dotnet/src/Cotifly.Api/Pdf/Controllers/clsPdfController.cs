using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Pdf.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Cotifly.Api.Pdf.Controllers;

/// <summary>
/// Descarga del PDF (<c>GET /pdfs/{archivo}</c>). Solo con <b>Bearer</b> (consola
/// web): dueño o <c>gerente_comercial</c> cuya regional efectiva incluye al dueño (misma regla que el listado regional); cualquier otro caso 404 <c>ErrorWeb</c>, igual que si no existiera.
/// Sin sesión, 401 (lo escriben los eventos de JwtBearer con <c>ErrorWeb</c>). El parámetro <c>?t=</c> de la URL firmada
/// (ADR-011 §3) se ignora.
/// </summary>
[ApiController]
[Route("pdfs")]
[Authorize(Policy = clsPoliticas.Comercial)]
public sealed class clsPdfController(IClsDescargaPdfService descarga) : ControllerBase
{
    /// <summary>200 <c>application/pdf</c> inline; 401 sin sesión; 404 <c>ErrorWeb</c>.</summary>
    [HttpGet("{archivo}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "application/pdf")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound, "application/json")]
    public async Task<IActionResult> Descargar(string archivo, CancellationToken cancellationToken)
    {
        var resultado = await descarga.ObtenerAsync(archivo, cancellationToken);
        if (resultado.EsFallo)
        {
            return clsRespuestasWeb.Error(resultado.Error);
        }

        var pdf = resultado.Valor;
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = pdf.Nombre, FileName = pdf.Nombre }.ToString();
        return File(pdf.Contenido, "application/pdf");
    }
}
