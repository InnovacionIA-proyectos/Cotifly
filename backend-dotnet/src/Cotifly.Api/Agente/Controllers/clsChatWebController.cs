using System.Text.RegularExpressions;
using Cotifly.Api.Agente.DTOs;
using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Precio;
using Cotifly.Dominio.Sesiones;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cotifly.Api.Agente.Controllers;

/// <summary>
/// Asistente de la consola web (<c>POST /web/chat</c>, port de <c>web_chat</c> de <c>orquestador/web.py</c>): un turno del agente
/// en la sesión <c>web/{sesion_id}</c>, ligada al comercial del token.
/// </summary>
[Route("web")]
public sealed partial class clsChatWebController(
    IClsContextoUsuario usuario,
    IClsAgenteRunner runner,
    ILogger<clsChatWebController> logger) : clsControladorWebBase(usuario)
{
    public const string MensajeSesionInvalida = "sesion_id inválido";
    public const string MensajeCotizacionOrigenInvalida = "cotizacion_origen_id inválido";
    public const string MensajeMonedaInvalida = "moneda inválida";
    public const int LargoMaximoMensaje = 8000;

    /// <summary>
    /// 200 <c>RespuestaChat</c> (o <c>RespuestaChatError</c> con <c>error: true</c> ante un fallo del agente o del proveedor de IA, o
    /// si la respuesta final no pasó la validación ni con el reintento y el turno no produjo PDF, precio ni margen, HU-003.10; si
    /// los produjo, <c>RespuestaChat</c> con ellos y el texto de respaldo);
    /// 400 <c>sesion_id</c>, <c>moneda</c> o cuerpo inválido; 401 sin sesión; 404 si la sesión es de otro comercial; 429 límite de la API.
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

        Guid? origen = null;
        if (!string.IsNullOrWhiteSpace(cuerpo.CotizacionOrigenId))
        {
            if (!Guid.TryParse(cuerpo.CotizacionOrigenId, out var id))
            {
                return BadRequest(new clsErrorWeb(MensajeCotizacionOrigenInvalida));
            }

            origen = id;
        }

        string? moneda = null;
        if (cuerpo.Moneda is not null)
        {
            var normalizada = clsConversorMoneda.Normalizar(cuerpo.Moneda);
            if (string.IsNullOrWhiteSpace(cuerpo.Moneda) || normalizada.EsFallo)
            {
                return BadRequest(new clsErrorWeb(MensajeMonedaInvalida));
            }

            moneda = normalizada.Valor;
        }

        var clave = clsSesion.IdWeb(cuerpo.SesionId);
        try
        {
            var resultado = await runner.EjecutarTurnoAsync(new clsSolicitudTurno(cuerpo.Mensaje, clave, clave, UsuarioId, origen, moneda), cancellationToken);
            if (resultado.EsFallo)
            {
                return resultado.Error.Codigo == clsCodigosAplicacion.NoEncontrado
                    ? NotFound(new clsErrorWeb(clsTextosAgente.SesionNoEncontrada))
                    : Error(resultado.Error);
            }

            // Respaldo del validador (HU-003.10): el modelo no dio una respuesta válida ni con el reintento. Si las herramientas del turno
            // ya produjeron datos (PDF, precio, margen), van en RespuestaChat con el texto de respaldo: el comercial no pierde el PDF
            // generado ni lo vuelve a generar. Sin datos, el error controlado.
            var respuesta = clsRespuestaChat.Desde(resultado.Valor);
            return resultado.Valor.EsRespaldo && respuesta is { Pdf: null, Precio: null, Margen: null }
                ? Ok(new clsRespuestaChatError(resultado.Valor.Texto))
                : Ok(respuesta);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // El frontend muestra el texto tal cual (paridad con el Python): 200 con error = true.
            logger.LogError(ex, "Fallo procesando el turno web de la sesión {Sesion}.", clave);
            return Ok(new clsRespuestaChatError(clsTextosAgente.ParaFallo(ex)));
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionSesion();
}
