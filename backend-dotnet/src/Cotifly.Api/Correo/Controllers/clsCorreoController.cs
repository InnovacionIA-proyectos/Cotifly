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
    IClsVistaPreviaCorreoService vistaPrevia,
    IClsHistorialCorreosService historial) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>RespuestaEnvio</c> (MailSender aceptó el correo: queda <c>encolado</c>); 400 <c>ErrorWeb</c> (destinatarios, asunto o cuerpo
    /// inválidos, también un asunto o una dirección con caracteres de control) o <c>ErrorNegocio</c> (duplicado, sin PDF, más de 20
    /// destinatarios con <c>CORREO_DEMASIADOS_DESTINATARIOS</c>, MailSender lo rechazó o no respondió); 404 si no es suya; 409 si la cotización
    /// cambió; 503 <c>ErrorNegocio</c> <c>CORREO_DESHABILITADO</c>.
    /// </summary>
    [HttpPost("enviar")]
    [ProducesResponseType<clsRespuestaEnvio>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Enviar(string folio, [FromBody] clsEnvioWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await correo.EnviarAsync(
            new clsEntradaEnviarCorreo(UsuarioId, cuerpo.Para ?? [], Folio: folio, Cc: cuerpo.Cc, Asunto: cuerpo.Asunto, Cuerpo: cuerpo.Cuerpo),
            cancellationToken);
        return resultado.Coincidir<IActionResult>(
            salida => Ok(new clsRespuestaEnvio(salida.Enviado, salida.MessageId)
            {
                CorreoId = salida.CorreoId == Guid.Empty ? null : salida.CorreoId,
                Estado = salida.Estado,
                RedirigidoA = salida.RedirigidoA,
            }),
            Error);
    }

    /// <summary>
    /// 200 <c>HistorialCorreos</c>, los envíos de la cotización con el estado de MailSender (<c>encolado</c>, <c>enviando</c>,
    /// <c>enviado</c> o <c>fallido</c> con su error); 404 si no es suya.
    /// </summary>
    [HttpGet("correos")]
    [ProducesResponseType<clsHistorialCorreosRespuesta>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Historial(string folio, CancellationToken cancellationToken)
    {
        var resultado = await historial.ListarAsync(UsuarioId, folio, cancellationToken);
        return resultado.Coincidir<IActionResult>(lista => Ok(new clsHistorialCorreosRespuesta(folio, lista)), Error);
    }

    /// <summary>
    /// Reintenta en MailSender un envío <c>fallido</c>. 200 <c>EnvioCorreo</c> (vuelve a <c>encolado</c>); 404 si la cotización
    /// no es suya o el envío no es de ella; 409 <c>CORREO_NO_REINTENTABLE</c>; 400 <c>CORREO_ENVIO_FALLIDO</c> si MailSender no respondió;
    /// 503 <c>CORREO_DESHABILITADO</c>.
    /// </summary>
    [HttpPost("correos/{correoId}/reintentar")]
    [ProducesResponseType<clsEnvioCorreoHistorial>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Reintentar(string folio, string correoId, CancellationToken cancellationToken)
    {
        // Un id que ni siquiera es un GUID responde 404, igual que uno que no existe (la sesión se exige antes).
        if (!Guid.TryParse(correoId, out var id))
        {
            return clsRespuestasWeb.NoEsTuya();
        }

        var resultado = await historial.ReintentarAsync(UsuarioId, folio, id, cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 200 <c>VistaPreviaCorreo</c> (no envía nada; el cuerpo es opcional); 404 si no es suya. El borrador se valida como en el
    /// envío: 400 <c>ErrorWeb</c> (asunto o destinatarios con caracteres de control, direcciones mal formadas, largos) o <c>ErrorNegocio</c>
    /// <c>CORREO_DEMASIADOS_DESTINATARIOS</c>.
    /// </summary>
    [HttpPost("correo/vista-previa")]
    [ProducesResponseType<clsVistaPreviaCorreo>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
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
