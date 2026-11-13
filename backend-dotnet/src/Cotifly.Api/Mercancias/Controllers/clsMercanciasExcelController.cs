using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Aplicacion.Mercancias.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Mercancias.Controllers;

/// <summary>
/// Excel de mercancías de una ruta (ADR-024, <c>specs/openapi.yaml</c>): Cotifly lo lee localmente, SIN Nexo, y devuelve
/// la grilla cruda para que el frontend mapee las columnas a tipo, cantidad, medidas y peso. Mismo rol que la captura (comercial o
/// gerente, la política de <see cref="clsControladorWebBase"/>) y el límite del cuerpo de las licitaciones (12 MB). El cuerpo se lee a
/// mano para que la sesión se valide antes que el tipo de contenido.
/// </summary>
[Route("web")]
public sealed class clsMercanciasExcelController(IClsContextoUsuario usuario, IClsAnalisisExcelMercanciasService analisis)
    : clsControladorWebBase(usuario)
{
    public const string ParteArchivo = "archivo";

    public const string MensajeMultipart = "Cuerpo inválido: envía multipart/form-data con el archivo en la parte 'archivo'.";

    /// <summary>
    /// 200 <c>AnalisisExcelMercancias</c>; 400 <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c>, <c>ARCHIVO_DEMASIADO_GRANDE</c>,
    /// <c>EXCEL_INVALIDO</c> o <c>EXCEL_SIN_DATOS</c>; 415 sin multipart; 503 <c>SERVICIO_OCUPADO</c>.
    /// </summary>
    [HttpPost("mercancias/analizar-excel")]
    [ProducesResponseType<clsAnalisisExcelMercancias>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Analizar(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new clsErrorWeb(MensajeMultipart));
        }

        var archivo = await clsArchivoMultipart.LeerParteAsync(Request, ParteArchivo, cancellationToken);
        var resultado = await analisis.AnalizarAsync(UsuarioId, archivo ?? new clsArchivoSubido(null, []), cancellationToken);
        return resultado.Coincidir<IActionResult>(a => Ok(a), Error);
    }
}
