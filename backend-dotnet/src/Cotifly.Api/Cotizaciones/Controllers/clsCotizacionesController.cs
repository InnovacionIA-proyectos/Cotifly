using Cotifly.Api.Comun;
using Cotifly.Api.Cotizaciones.DTOs;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Cotizaciones.Controllers;

/// <summary>
/// Cotizaciones del comercial (<c>specs/openapi.yaml</c>): listado, detalle, seguimiento y datos para una versión nueva. Solo las del
/// comercial autenticado; una ajena responde 404 igual que una inexistente. Excepción: <c>GET /web/cotizaciones/regional</c> (HU-016.1),
/// solo para <c>gerente_comercial</c>, lista las de los miembros de su regional.
/// </summary>
[Route("web")]
public sealed class clsCotizacionesController(
    IClsContextoUsuario usuario,
    IClsMisCotizacionesService misCotizaciones,
    IClsSeguimientoService seguimiento,
    IClsNuevaVersionService nuevaVersion,
    IClsCotizacionesRegionalService regional,
    IClsDetalleCotizacionService detalle) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// HU-027.1 (#179). 200 <c>DetalleCotizacion</c> con el <c>ETag</c>: cabecera, costeo por ruta, rutas capturadas y totales en una llamada
    /// ("Ver detalle"). El dueño, o un gerente comercial de la regional del dueño en solo consulta; 404 en cualquier otro caso.
    /// </summary>
    [HttpGet("cotizaciones/{folio}")]
    [ProducesResponseType<clsDetalleCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Detalle(string folio, CancellationToken cancellationToken) =>
        (await detalle.ObtenerAsync(folio, cancellationToken)).Coincidir<IActionResult>(d => Ok(d), Error);

    /// <summary>200 <c>MisCotizaciones</c>: máximo 100, de la más reciente a la más antigua.</summary>
    [HttpGet("mis-cotizaciones")]
    [ProducesResponseType<clsMisCotizaciones>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken) =>
        Ok(await misCotizaciones.ListarAsync(UsuarioId, cancellationToken));

    /// <summary>
    /// HU-016.1 (#154). 200 <c>CotizacionesRegional</c>: las de los miembros de la regional del gerente, máximo 100, de la más reciente a
    /// la más antigua; 403 si no es gerente; 409 <c>REGIONAL_NO_ASIGNADA</c>; 503 <c>REGIONAL_NO_DISPONIBLE</c>.
    /// </summary>
    [HttpGet("cotizaciones/regional")]
    [Authorize(Policy = clsPoliticas.GerenteComercial)]
    [ProducesResponseType<clsCotizacionesRegional>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListarRegional(CancellationToken cancellationToken)
    {
        var resultado = await regional.ListarAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }

    /// <summary>200 <c>CotizacionResumen</c>; 400 motivo de más de 2000 caracteres; 404 si no es suya.</summary>
    [HttpPost("cotizaciones/{folio}/seguimiento")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarcarSeguimiento(string folio, [FromBody] clsSeguimientoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await seguimiento.MarcarAsync(UsuarioId, folio, cuerpo.Ganada, cuerpo.MotivoRechazo, cancellationToken);
        return resultado.EsFallo ? Error(resultado.Error) : Ok(await misCotizaciones.ResumirAsync(resultado.Valor, cancellationToken));
    }

    /// <summary>200 <c>DatosNuevaVersion</c> (no modifica la original); 404 si no existe, no es suya o el id no es un UUID.</summary>
    [HttpGet("cotizaciones/{id}/nueva-version")]
    [ProducesResponseType<clsDatosNuevaVersion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DatosNuevaVersion(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var cotizacionId))
        {
            return clsRespuestasWeb.NoEsTuya();
        }

        var resultado = await nuevaVersion.ObtenerDatosAsync(UsuarioId, cotizacionId, cancellationToken);
        return resultado.Coincidir<IActionResult>(datos => Ok(datos), Error);
    }
}
