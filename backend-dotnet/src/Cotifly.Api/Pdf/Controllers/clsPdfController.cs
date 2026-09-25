using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Cotifly.Api.Pdf.Controllers;

/// <summary>
/// Descarga del PDF (<c>GET /pdfs/{archivo}</c>; en el Python era público, hallazgo corregido). Dos formas de acceso:
/// <list type="bullet">
/// <item><b>Bearer</b> (consola web): dueño o <c>gerente_comercial</c> de la misma regional; cualquier otro caso 404 <c>ErrorWeb</c>,
/// igual que si no existiera. Sin sesión, 401.</item>
/// <item><b>URL firmada</b> (<c>?t=...</c>, Google Chat, ADR-011 §3): HMAC de 15 minutos sobre el archivo y el comercial, que debe
/// ser el dueño. Token alterado o vencido → 401.</item>
/// </list>
/// </summary>
[ApiController]
[Route("pdfs")]
[AllowAnonymous]
public sealed class clsPdfController(
    IClsDescargaPdfService descarga,
    IClsDescargaPdfFirmadaService descargaFirmada,
    IAuthorizationService autorizacion) : ControllerBase
{
    /// <summary>200 <c>application/pdf</c> inline; 401 sin sesión (o enlace firmado inválido); 404 <c>ErrorWeb</c>.</summary>
    [HttpGet("{archivo}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "application/pdf")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized, "application/json")]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound, "application/json")]
    public async Task<IActionResult> Descargar(string archivo, [FromQuery(Name = "t")] string? t, CancellationToken cancellationToken)
    {
        clsResultado<clsArchivoPdf> resultado;
        if (!string.IsNullOrEmpty(t))
        {
            resultado = await descargaFirmada.ObtenerAsync(archivo, t, cancellationToken);
            if (resultado.EsFallo && resultado.Error.Codigo == clsCodigosAplicacion.NoAutenticado)
            {
                return Unauthorized(new clsErrorWeb(resultado.Error.Mensaje));
            }
        }
        else
        {
            // Sin enlace firmado: la misma política Comercial de antes (el 401/403 lo escriben los eventos de JwtBearer con ErrorWeb).
            var permitido = await autorizacion.AuthorizeAsync(User, clsPoliticas.Comercial);
            if (!permitido.Succeeded)
            {
                return User.Identity?.IsAuthenticated == true ? Forbid() : Challenge();
            }

            resultado = await descarga.ObtenerAsync(archivo, cancellationToken);
        }

        if (resultado.EsFallo)
        {
            return clsRespuestasWeb.Error(resultado.Error);
        }

        var pdf = resultado.Valor;
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = pdf.Nombre, FileName = pdf.Nombre }.ToString();
        return File(pdf.Contenido, "application/pdf");
    }
}
