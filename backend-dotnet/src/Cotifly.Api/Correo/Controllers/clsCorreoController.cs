using Cotifly.Api.Comun;
using Cotifly.Api.Correo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Cotifly.Api.Correo.Controllers;

/// <summary>
/// Envío de la cotización por correo y su vista previa (<c>specs/openapi.yaml</c>). El remitente y dueño exigido es el comercial
/// del token; una cotización ajena responde 404 igual que una inexistente.
/// </summary>
[Route("web/cotizaciones/{folio}")]
public sealed class clsCorreoController(
    IClsContextoUsuario usuario,
    IClsCorreoService correo,
    IClsVistaPreviaCorreoService vistaPrevia) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>RespuestaEnvio</c>; 400 <c>ErrorWeb</c> (destinatarios, asunto o cuerpo inválidos) o <c>ErrorNegocio</c> (duplicado,
    /// sin PDF, fallo del relay); 404 si no es suya; 503 <c>ErrorNegocio</c> <c>CORREO_DESHABILITADO</c>.
    /// </summary>
    [HttpPost("enviar")]
    [ProducesResponseType<clsRespuestaEnvio>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Enviar(string folio, [FromBody] clsEnvioWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await correo.EnviarAsync(
            new clsEntradaEnviarCorreo(UsuarioId, cuerpo.Para ?? [], Folio: folio, Cc: cuerpo.Cc, Asunto: cuerpo.Asunto, Cuerpo: cuerpo.Cuerpo),
            cancellationToken);
        return resultado.Coincidir<IActionResult>(
            salida => Ok(new clsRespuestaEnvio(salida.Enviado, salida.MessageId)),
            Error);
    }

    /// <summary>200 <c>VistaPreviaCorreo</c> (no envía nada; el cuerpo es opcional); 404 si no es suya.</summary>
    [HttpPost("correo/vista-previa")]
    [ProducesResponseType<clsVistaPreviaCorreo>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VistaPrevia(
        string folio,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] clsEnvioWebRequest? cuerpo,
        CancellationToken cancellationToken)
    {
        var borrador = cuerpo is null ? null : new clsBorradorCorreo(cuerpo.Para, cuerpo.Cc, cuerpo.Asunto, cuerpo.Cuerpo);
        var resultado = await vistaPrevia.PrevisualizarAsync(UsuarioId, folio, borrador, cancellationToken);
        return resultado.Coincidir<IActionResult>(v => Ok(v), Error);
    }
}
