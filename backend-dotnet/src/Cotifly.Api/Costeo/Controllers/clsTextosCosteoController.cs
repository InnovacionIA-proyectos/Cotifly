using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Cotifly.Api.Costeo.Controllers;

/// <summary>
/// Asunto e introducción del PDF del costeo (ADR-018 §2a, <c>specs/openapi.yaml</c>): la IA los redacta con el texto
/// estándar de respaldo y el comercial los edita. Solo el dueño (404 si no es suya); <c>If-Match</c> obligatorio (428/409) y la regla
/// del PDF (ADR-018 §11). Las respuestas 200 traen el <c>ETag</c> nuevo.
/// </summary>
[Route("web")]
public sealed class clsTextosCosteoController(IClsContextoUsuario usuario, IClsTextosCosteoService textos) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>TextosCotizacion</c> (de la IA o los estándar si la IA tardó, falló o su salida no pasó la validación: nunca un error por
    /// la IA); 400 <c>SOLICITUD_INVALIDA</c> (<c>indicaciones_redaccion</c> de más de 500 caracteres); 404; 409
    /// <c>TEXTOS_EDITADOS</c> (salvo <c>?sobrescribir=true</c>), <c>PDF_YA_ENVIADO</c> o <c>CONFLICTO_CONCURRENCIA</c>; 428 sin <c>If-Match</c>.
    /// El cuerpo es opcional: sin él, la IA redacta sin indicaciones.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/costeo/textos/redactar")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsTextosCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Redactar(
        string folio,
        [FromQuery] bool sobrescribir,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] clsRedactarTextosWebRequest? cuerpo,
        CancellationToken cancellationToken) =>
        Responder(await textos.RedactarAsync(
            UsuarioId, folio, sobrescribir, clsConcurrenciaHttp.VersionDe(HttpContext), cuerpo?.IndicacionesRedaccion, cancellationToken));

    /// <summary>
    /// 200 <c>TextosCotizacion</c> con <c>editado = true</c>; 400 <c>TEXTOS_COTIZACION_INVALIDOS</c>; 404; 409 <c>PDF_YA_ENVIADO</c> o
    /// <c>CONFLICTO_CONCURRENCIA</c>; 428 sin <c>If-Match</c>. Con el PDF vigente sin enviar, lo invalida.
    /// </summary>
    [HttpPut("cotizaciones/{folio}/costeo/textos")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsTextosCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Editar(string folio, [FromBody] clsTextosCotizacionWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        return Responder(await textos.EditarAsync(
            UsuarioId, folio, cuerpo.Asunto, cuerpo.Introduccion, clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken));
    }

    private IActionResult Responder(clsResultado<clsTextosGuardados> resultado)
    {
        if (resultado.EsFallo)
        {
            return Error(resultado.Error);
        }

        clsConcurrenciaHttp.EscribirEtag(Response, resultado.Valor.RowVersion);
        return Ok(resultado.Valor.Textos);
    }
}
