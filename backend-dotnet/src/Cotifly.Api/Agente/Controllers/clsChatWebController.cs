using System.Text.RegularExpressions;
using Cotifly.Api.Agente.DTOs;
using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Sesiones;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cotifly.Api.Agente.Controllers;

/// <summary>
/// Asistente de la consola web (<c>POST /web/chat</c>). Trabaja sobre UNA cotización (ADR-018 §2b): con
/// <c>folio</c> corre el asistente de solo lectura y propuesta (<see cref="IClsAsistenteFolioRunner"/>) en la sesión
/// <c>web/{sesion_id}</c> ligada al comercial del token y al folio; sin <c>folio</c> responde siempre 400 <c>FOLIO_REQUERIDO</c>.
/// </summary>
[Route("web")]
public sealed partial class clsChatWebController(
    IClsContextoUsuario usuario,
    IClsAsistenteFolioRunner asistente,
    ILogger<clsChatWebController> logger) : clsControladorWebBase(usuario)
{
    public const string MensajeSesionInvalida = "sesion_id inválido";
    public const string MensajeFolioInvalido = "folio inválido";
    public const string MensajeFolioRequerido = "Abre el asistente desde una cotización: falta el folio.";
    public const int LargoMaximoMensaje = 8000;

    /// <summary>
    /// 200 <c>RespuestaChat</c> (o <c>RespuestaChatError</c> con <c>error: true</c> ante un fallo del asistente o del proveedor de IA,
    /// o si la respuesta final no pasó la validación ni con el reintento y no hubo propuesta);
    /// 400 <c>sesion_id</c>, <c>folio</c> o cuerpo inválido, o <c>FOLIO_REQUERIDO</c> sin folio; 401 sin sesión;
    /// 404 si la sesión es de otro comercial o el folio no es suyo; 429 límite de la API.
    /// </summary>
    [HttpPost("chat")]
    [EnableRateLimiting(clsConfiguracionAutenticacion.PoliticaLimiteChat)]
    [ProducesResponseType<clsRespuestaChat>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enviar([FromBody] clsMensajeWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        if (string.IsNullOrWhiteSpace(cuerpo.Mensaje) || cuerpo.Mensaje.Length > LargoMaximoMensaje)
        {
            return BadRequest(new clsErrorWeb($"{clsErrorWeb.MensajeCuerpoInvalido} Revisa: mensaje."));
        }

        if (cuerpo.SesionId is null || !ExpresionSesion().IsMatch(cuerpo.SesionId))
        {
            return BadRequest(new clsErrorWeb(MensajeSesionInvalida));
        }

        if (!string.IsNullOrWhiteSpace(cuerpo.Folio))
        {
            return cuerpo.Folio.Trim().Length > clsCotizacionSolicitud.LargoMaximoFolio
                ? BadRequest(new clsErrorWeb(MensajeFolioInvalido))
                : await AsistenteAsync(cuerpo.Mensaje, cuerpo.SesionId, cuerpo.Folio.Trim(), cancellationToken);
        }

        return Error(new clsErrorDominio(clsCodigosError.FolioRequerido, MensajeFolioRequerido));
    }

    /// <summary>
    /// Turno del asistente por folio: 200 <c>RespuestaChat</c> con <c>accion_propuesta</c> (o <c>RespuestaChatError</c> ante un fallo del
    /// agente o del proveedor, o si el validador no obtuvo respuesta y no hubo propuesta); 404 si el folio no es del comercial o la
    /// sesión es de otro. Ni el texto ni la propuesta cambian datos: el frontend ejecuta la propuesta al pulsar Confirmar.
    /// </summary>
    private async Task<IActionResult> AsistenteAsync(string mensaje, string sesionId, string folio, CancellationToken cancellationToken)
    {
        var espacio = clsSesion.IdWeb(sesionId);
        var hilo = clsSesion.IdWebFolio(sesionId, folio);
        try
        {
            var resultado = await asistente.EjecutarTurnoAsync(new clsSolicitudTurnoFolio(mensaje, espacio, hilo, UsuarioId, folio), cancellationToken);
            if (resultado.EsFallo)
            {
                return resultado.Error.Codigo == clsCodigosAplicacion.NoEncontrado
                    ? NotFound(new clsErrorWeb(clsTextosAgente.SesionNoEncontrada))
                    : Error(resultado.Error);
            }

            var turno = resultado.Valor;
            return turno.EsRespaldo && turno.AccionPropuesta is null
                ? Ok(new clsRespuestaChatError(turno.Texto, clsTextosAgente.CodigoRespuestaNoEstructurada))
                : Ok(clsRespuestaChat.DesdeAsistente(turno, clsTextosAgente.PropuestaSinTexto));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Fallo procesando el turno del asistente de la sesión {Sesion}.", hilo);
            return Ok(new clsRespuestaChatError(clsTextosAgente.ParaFallo(ex), clsTextosAgente.CodigoDeFallo(ex)));
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionSesion();
}
