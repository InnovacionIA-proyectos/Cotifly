using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Costeo.Controllers;

/// <summary>
/// Costeo en Nexo desde la consola del comercial (ADR-015, <c>specs/openapi.yaml</c>): envío del formulario ampliado y reintento.
/// Solo cotizaciones propias; una ajena responde 404 igual que una inexistente.
/// </summary>
[Route("web")]
public sealed class clsCosteoController(
    IClsContextoUsuario usuario,
    IClsEnvioCosteoService envio,
    IClsMisCotizacionesService misCotizaciones) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 201 <c>CotizacionResumen</c> aunque Nexo falle (queda en <c>error_envio</c> con el motivo); 400 <c>SOLICITUD_COSTEO_INVALIDA</c>,
    /// <c>MARGEN_LINEA_DESCONOCIDA</c> o <c>FOLIO_CONSECUTIVO_AGOTADO</c> (nada queda guardado).
    /// </summary>
    [HttpPost("solicitudes-costeo")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Enviar([FromBody] clsSolicitudCosteoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var entrada = cuerpo.AEntrada();
        if (entrada.EsFallo)
        {
            return Error(entrada.Error);
        }

        var resultado = await envio.EnviarAsync(UsuarioId, entrada.Valor, cancellationToken);
        return resultado.EsFallo
            ? Error(resultado.Error)
            : StatusCode(StatusCodes.Status201Created, await misCotizaciones.ResumirAsync(resultado.Valor, CancellationToken.None));
    }

    /// <summary>200 <c>CotizacionResumen</c> tras el reenvío; 404 si no es suya; 409 <c>COSTEO_NO_REINTENTABLE</c>.</summary>
    [HttpPost("cotizaciones/{folio}/costeo/reintentar")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reintentar(string folio, CancellationToken cancellationToken)
    {
        var resultado = await envio.ReintentarAsync(UsuarioId, folio, cancellationToken);
        return resultado.EsFallo
            ? Error(resultado.Error)
            : Ok(await misCotizaciones.ResumirAsync(resultado.Valor, CancellationToken.None));
    }
}
