using System.Globalization;
using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Costeo.Controllers;

/// <summary>
/// HU-028 (#186, ADR-021): el comercial edita la solicitud ("Editar solicitud"). Solo cotizaciones propias; una ajena responde 404 igual
/// que una inexistente.
/// </summary>
[Route("web")]
public sealed class clsEdicionSolicitudController(IClsContextoUsuario usuario, IClsEdicionSolicitudService edicion) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>SolicitudEditable</c> con el <c>ETag</c>: el modelo editable completo por ruta, los campos bloqueados y cómo se aplicaría la
    /// edición ahora (<c>edicion.modo</c>, o <c>permitida = false</c> con el motivo); 404.
    /// </summary>
    [HttpGet("cotizaciones/{folio}/solicitud")]
    [ProducesResponseType<clsSolicitudEditable>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(string folio, CancellationToken cancellationToken)
    {
        var resultado = await edicion.ObtenerAsync(UsuarioId, folio, cancellationToken);
        if (resultado.EsFallo)
        {
            return Error(resultado.Error);
        }

        clsConcurrenciaHttp.EscribirEtag(Response, resultado.Valor.VersionNegocio.ToString(CultureInfo.InvariantCulture));
        return Ok(resultado.Valor);
    }

    /// <summary>
    /// 200 <c>EdicionSolicitudResultado</c> (<c>en_sitio</c> o <c>reemplazo</c>, o sin cambios) o 201 con <c>Location</c> (<c>nueva_version</c>),
    /// los dos con el <c>ETag</c> de la cotización resultante; 400 <c>CAMPO_NO_EDITABLE</c> (<c>detalle.campo</c>) o
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (<c>detalle.errores</c>); 403 <c>NEXO_SIN_PERMISO</c>; 404; 409 <c>CONFLICTO_CONCURRENCIA</c>,
    /// <c>ESTADO_NO_PERMITE</c> (<c>detalle.motivo</c>) o <c>IDEMPOTENCY_KEY_REUTILIZADA</c>; 428 sin <c>If-Match</c>; 503 si un catálogo,
    /// el cubicador, la TRM o el rechazo en Nexo no responden (nada se aplicó ni se capturó).
    /// </summary>
    [HttpPut("cotizaciones/{folio}/solicitud")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsResultadoEdicionSolicitud>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsResultadoEdicionSolicitud>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Editar(string folio, [FromBody] clsEdicionSolicitudWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var clave = Request.Headers[clsCosteoController.EncabezadoIdempotencia].ToString();
        var resultado = await edicion.EditarAsync(
            UsuarioId, folio, cuerpo.AEntrada(), clsConcurrenciaHttp.VersionDe(HttpContext), clave.Length == 0 ? null : clave, cancellationToken);
        if (resultado.EsFallo)
        {
            return Error(resultado.Error);
        }

        var valor = resultado.Valor;
        clsConcurrenciaHttp.EscribirEtag(Response, valor.RowVersion);
        return valor.Creada
            ? Created($"/web/cotizaciones/{valor.Respuesta.Folio}/costeo", valor.Respuesta)
            : Ok(valor.Respuesta);
    }
}
