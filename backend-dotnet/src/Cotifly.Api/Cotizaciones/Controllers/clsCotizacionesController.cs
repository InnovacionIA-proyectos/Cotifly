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
/// comercial autenticado; una ajena responde 404 igual que una inexistente. Excepción: <c>GET /web/cotizaciones/regional</c>,
/// solo para <c>gerente_comercial</c>, lista las de los miembros de su regional.
/// </summary>
[Route("web")]
public sealed class clsCotizacionesController(
    IClsContextoUsuario usuario,
    IClsMisCotizacionesService misCotizaciones,
    IClsSeguimientoService seguimiento,
    IClsNuevaVersionService nuevaVersion,
    IClsCotizacionesRegionalService regional,
    IClsDetalleCotizacionService detalle,
    IClsCatalogoMotivosPerdida motivosPerdida,
    IClsExportacionCotizacionService exportacion) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 xlsx de la cotización: el dueño recibe la versión interna (por ruta y por camión: costo, margen,
    /// precio, conexos y totales); el gerente de su regional y el director, la misma en solo lectura y sin costos ni márgenes. 404 en
    /// cualquier otro caso (o sin rutas); 409 <c>COSTEO_NO_LISTO</c> sin precio.
    /// </summary>
    [HttpGet("cotizaciones/{folio}/excel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Excel(string folio, CancellationToken cancellationToken) =>
        (await exportacion.ExportarAsync(folio, cancellationToken)).Coincidir<IActionResult>(a => File(a.Contenido, a.TipoContenido, a.Nombre), Error);

    /// <summary>
    /// 200 <c>MotivosPerdida</c>: la lista configurada de motivos de pérdida (la de Nexo), en su orden, con el
    /// que exige comentario (<c>Otro</c>). No depende de Nexo.
    /// </summary>
    [HttpGet("motivos-perdida")]
    [ProducesResponseType<clsMotivosPerdida>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    public IActionResult MotivosPerdida() => Ok(motivosPerdida.Listar());

    /// <summary>
    /// 200 <c>DetalleCotizacion</c> con el <c>ETag</c>: cabecera, costeo por ruta, rutas capturadas y totales en una llamada
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
    /// 200 <c>CotizacionesRegional</c>: las de los miembros de la regional del gerente, máximo 100, de la más reciente a
    /// la más antigua; 403 si no es gerente ni director, o <c>REGIONAL_NO_PERMITIDA</c> si el gerente pide otra regional; 409
    /// <c>REGIONAL_NO_ASIGNADA</c>; 503 <c>REGIONAL_NO_DISPONIBLE</c>. El director ve todas o la de <c>regional</c>.
    /// </summary>
    [HttpGet("cotizaciones/regional")]
    [Authorize(Policy = clsPoliticas.GerenteComercial)]
    [ProducesResponseType<clsCotizacionesRegional>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListarRegional([FromQuery(Name = "regional")] string? regionalPedida, CancellationToken cancellationToken)
    {
        var resultado = await regional.ListarAsync(regionalPedida, cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }

    /// <summary>
    /// 200 <c>CotizacionResumen</c>; 400 motivo de más de 2000 caracteres o comentario de más de 500, y en una perdida
    /// <c>MOTIVO_NO_PERMITIDO</c> si el motivo no es de la lista o <c>Otro</c> viene sin comentario; 404 si no es suya.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/seguimiento")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarcarSeguimiento(string folio, [FromBody] clsSeguimientoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await seguimiento.MarcarAsync(UsuarioId, folio, cuerpo.Ganada, cuerpo.MotivoRechazo, cuerpo.Comentario, cancellationToken);
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
