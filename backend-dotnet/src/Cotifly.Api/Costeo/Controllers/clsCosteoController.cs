using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
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
    IClsMisCotizacionesService misCotizaciones,
    IClsCosteoCotizacionService costeo,
    IClsAjusteMargenCosteoService ajusteMargen,
    IClsResultadoCotizacionService resultadoCotizacion,
    IClsReSolicitudCosteoService reSolicitud) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>CotizacionResumen</c> con <c>ganada</c> y <c>motivo_rechazo</c> (solo si Nexo respondió 200); 400; 404 si no es suya;
    /// 409 <c>RESULTADO_YA_DEFINIDO</c>, <c>COTIZACION_NO_ENTREGADA</c>, <c>COSTEO_NO_LISTO</c> o <c>CONFLICTO_CONCURRENCIA</c>; 428 en la
    /// perdida solo en Cotifly sin <c>If-Match</c> (HU-013.17); 503 si Nexo no responde. El 200 trae el <c>ETag</c> nuevo.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/resultado")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> MarcarResultado(string folio, [FromBody] clsResultadoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        // If-Match opcional (compatibilidad); la perdida solo en Cotifly (HU-013.17) lo exige: 428 sin él, 409 si no coincide.
        var resultado = await resultadoCotizacion.MarcarAsync(
            UsuarioId, folio, cuerpo.Resultado, cuerpo.Motivo, cuerpo.Comentario, clsConcurrenciaHttp.LeerIfMatch(Request), cancellationToken);
        if (resultado.EsExitoso)
        {
            clsConcurrenciaHttp.EscribirEtag(Response, resultado.Valor.VersionNegocio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return resultado.EsFallo ? Error(resultado.Error) : Ok(await misCotizaciones.ResumirAsync(resultado.Valor, CancellationToken.None));
    }

    /// <summary>200 <c>CosteoCotizacion</c> (estado, precio final y servicios conexos con su precio; nunca el costo de Nexo); 404 si no es suya.</summary>
    [HttpGet("cotizaciones/{folio}/costeo")]
    [ProducesResponseType<clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(string folio, CancellationToken cancellationToken) =>
        (await costeo.ObtenerAsync(UsuarioId, folio, cancellationToken)).Coincidir<IActionResult>(c => Ok(c), Error);

    /// <summary>
    /// HU-013.2 (ADR-018 §3): 200 <c>CosteoCotizacion</c> con el margen ajustado y los precios recalculados (y el <c>ETag</c> nuevo);
    /// 400 <c>AJUSTE_MARGEN_INVALIDO</c> o <c>JUSTIFICACION_REQUERIDA</c>; 404; 409 <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_ENVIADO</c> o
    /// <c>CONFLICTO_CONCURRENCIA</c>; 428 sin <c>If-Match</c>.
    /// </summary>
    [HttpPut("cotizaciones/{folio}/costeo/margen")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> AjustarMargen(string folio, [FromBody] clsAjusteMargenWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        if (cuerpo.Margen is not { } margen)
        {
            return Error(new clsErrorDominio(clsCodigosError.AjusteMargenInvalido, "El campo 'margen' es obligatorio (fracción: 0.18 = 18 %)."));
        }

        var resultado = await ajusteMargen.AjustarAsync(UsuarioId, folio, margen, cuerpo.Justificacion, clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 200 <c>CosteoCotizacion</c> con la selección (y el <c>ETag</c> nuevo); 400 id desconocido; 404; 409 <c>COSTEO_NO_LISTO</c>,
    /// <c>PDF_YA_ENVIADO</c> o <c>CONFLICTO_CONCURRENCIA</c>; 428 sin <c>If-Match</c>. Con PDF vigente lo invalida (ADR-018 §11).
    /// </summary>
    [HttpPut("cotizaciones/{folio}/costeo/servicios-conexos")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> SeleccionarServiciosConexos(
        string folio, [FromBody] clsSeleccionServiciosConexosWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await costeo.SeleccionarServiciosConexosAsync(
            UsuarioId, folio, cuerpo.Incluidos ?? [], clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 201 <c>SalidaPdf</c> (y el <c>ETag</c> nuevo); 404; 409 <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_GENERADO</c>, <c>PDF_YA_ENVIADO</c> o
    /// <c>CONFLICTO_CONCURRENCIA</c> (solo con <c>If-Match</c>, que aquí es opcional); 400 <c>PDF_PAYLOAD_INVALIDO</c>; 503
    /// <c>PDF_RENDER_NO_DISPONIBLE</c> (falló el render o no hubo turno a tiempo, #92/#95).
    /// </summary>
    [HttpPost("cotizaciones/{folio}/costeo/pdf")]
    [ProducesResponseType<Aplicacion.Pdf.DTOs.clsSalidaPdf>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GenerarPdf(string folio, CancellationToken cancellationToken)
    {
        var resultado = await costeo.GenerarPdfAsync(UsuarioId, folio, clsConcurrenciaHttp.LeerIfMatch(Request), cancellationToken);
        if (resultado.EsFallo)
        {
            return Error(resultado.Error);
        }

        clsConcurrenciaHttp.EscribirEtag(Response, resultado.Valor.RowVersion);
        return StatusCode(StatusCodes.Status201Created, resultado.Valor.Pdf);
    }

    /// <summary>
    /// 201 <c>CotizacionResumen</c> aunque Nexo falle (queda en <c>error_envio</c> con el motivo); 400 <c>SOLICITUD_COSTEO_INVALIDA</c>
    /// (con todos los errores en <c>detalle.errores</c>, HU-021.6), <c>MARGEN_LINEA_DESCONOCIDA</c> o <c>FOLIO_CONSECUTIVO_AGOTADO</c>; 503
    /// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>, <c>MUNICIPIOS_NO_DISPONIBLE</c> o <c>NEXO_NO_DISPONIBLE</c> (conexos) si no se pudo validar
    /// contra los catálogos de Nexo (nada queda guardado).
    /// </summary>
    [HttpPost("solicitudes-costeo")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Enviar([FromBody] clsSolicitudCosteoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var entrada = cuerpo.AEntrada();
        if (entrada.EsFallo)
        {
            return Error(entrada.Error);
        }

        var resultado = await envio.CapturarAsync(UsuarioId, entrada.Valor, cancellationToken);
        return resultado.EsFallo
            ? Error(resultado.Error)
            : StatusCode(StatusCodes.Status201Created, await misCotizaciones.ResumirAsync(resultado.Valor, CancellationToken.None));
    }

    /// <summary>Encabezado de la clave de idempotencia (ADR-018 §8).</summary>
    public const string EncabezadoIdempotencia = "Idempotency-Key";

    /// <summary>
    /// HU-013.4 (ADR-018 §5), "Cotizar costo": 200 <c>CotizacionResumen</c> si se reenvió en el mismo folio o 201 con <c>Location</c> si
    /// se creó la versión <c>-vN</c> (los dos con el <c>ETag</c> de la cotización resultante); 400 <c>SOLICITUD_COSTEO_INVALIDA</c> o
    /// <c>ErrorWeb</c> (<c>Idempotency-Key</c> reutilizada con otro cuerpo); 404; 409 <c>ESTADO_NO_PERMITE</c>, <c>PDF_YA_ENVIADO</c> o
    /// <c>CONFLICTO_CONCURRENCIA</c>; 428 sin <c>If-Match</c>.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/costeo/re-solicitar")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> ReSolicitar(string folio, [FromBody] clsReSolicitudCosteoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var entrada = cuerpo.AEntrada();
        if (entrada.EsFallo)
        {
            return Error(entrada.Error);
        }

        var clave = Request.Headers[EncabezadoIdempotencia].ToString();
        var resultado = await reSolicitud.ReSolicitarAsync(
            UsuarioId, folio, entrada.Valor, clsConcurrenciaHttp.VersionDe(HttpContext), clave.Length == 0 ? null : clave, cancellationToken);
        if (resultado.EsFallo)
        {
            return Error(resultado.Error);
        }

        var valor = resultado.Valor;
        clsConcurrenciaHttp.EscribirEtag(Response, valor.RowVersion);

        // HU-013.15 (#124): la respuesta avisa siempre (lista vacía si no había) los conexos del costeo anterior que se descartan.
        var resumen = valor.Cotizacion with { ConexosDescartados = valor.ConexosDescartados ?? [] };
        return valor.VersionNueva
            ? Created($"/web/cotizaciones/{valor.Cotizacion.Folio}/costeo", resumen)
            : Ok(resumen);
    }

    /// <summary>
    /// 200 <c>CotizacionResumen</c> tras el reenvío; 404 si no es suya; 409 <c>COSTEO_NO_REINTENTABLE</c>. Se conserva por compatibilidad:
    /// <c>POST .../costeo/re-solicitar</c> lo amplía (HU-013.4).
    /// </summary>
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
