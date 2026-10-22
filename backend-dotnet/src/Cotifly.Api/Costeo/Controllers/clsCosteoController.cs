using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
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
    IClsReSolicitudCosteoService reSolicitud,
    ILogger<clsCosteoController> logger,
    IClsRecalculoVehiculosService recalculoVehiculos) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// HU-023.3 (#158): 200 <c>CosteoCotizacion</c> con los camiones de la ruta recalculados por el cubicador (cuando cambiaron sus
    /// mercancías o su peso, antes de enviar a Nexo); 400 <c>SOLICITUD_COSTEO_INVALIDA</c> (mercancías sin código), <c>RUTA_INEXISTENTE</c>
    /// o un rechazo del cubicador (<c>MERCANCIA_DESCONOCIDA</c>...); 404; 409 <c>ESTADO_NO_PERMITE</c> (ya está en Nexo: usar
    /// re-solicitar) o <c>CONFLICTO_CONCURRENCIA</c>; 428 sin <c>If-Match</c>; 503 si el cubicador no responde (sin respaldo) o su
    /// respuesta es inválida.
    /// </summary>
    [HttpPost("cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos")]
    [clsRequiereIfMatch]
    [ProducesResponseType<clsCosteoCotizacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecalcularVehiculos(string folio, string rutaId, CancellationToken cancellationToken)
    {
        // Endurecimiento A (#199): un id de ruta que no es un GUID responde 404, igual que una ruta que no existe (antes 400 "Cuerpo inválido").
        if (!Guid.TryParse(rutaId, out var ruta))
        {
            return clsRespuestasWeb.NoEsTuya();
        }

        var resultado = await recalculoVehiculos.RecalcularAsync(UsuarioId, folio, ruta, clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 200 <c>CotizacionResumen</c> con <c>ganada</c> y <c>motivo_rechazo</c> (Nexo respondió 200, o HU-033: Nexo aún no la tiene
    /// entregada y el resultado queda pendiente de sincronizar); 400; 404 si no es suya; 409 <c>RESULTADO_YA_DEFINIDO</c>,
    /// <c>COTIZACION_NO_ENTREGADA</c> (ganada en <c>error_envio</c>, <c>pendiente_envio</c> ya en Nexo, o error de Nexo al leer el costeo), <c>COSTEO_NO_LISTO</c> o <c>CONFLICTO_CONCURRENCIA</c>; 428 en la
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
    /// HU-013.2 (ADR-018 §3; por ruta desde HU-021.8, #135): 200 <c>CosteoCotizacion</c> con el margen de la ruta ajustado y los precios
    /// recalculados (y el <c>ETag</c> nuevo); 400 <c>AJUSTE_MARGEN_INVALIDO</c>, <c>JUSTIFICACION_REQUERIDA</c>, <c>RUTA_REQUERIDA</c> (con
    /// <c>detalle.rutas</c>) o <c>RUTA_INEXISTENTE</c>; 404; 409 <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_ENVIADO</c> o
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

        var resultado = await ajusteMargen.AjustarAsync(
            UsuarioId, folio, new clsRutaIndicada(cuerpo.LineaOrden, cuerpo.LineaSolicitudId), margen, cuerpo.Justificacion,
            clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }

    /// <summary>
    /// 200 <c>CosteoCotizacion</c> con los conexos de la ruta quitados o con su cantidad nueva (y el <c>ETag</c> nuevo; HU-021.8, #135);
    /// 400 id desconocido, forma inválida, <c>RUTA_REQUERIDA</c> o <c>RUTA_INEXISTENTE</c>; 404; 409 <c>COSTEO_NO_LISTO</c>,
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
            UsuarioId, folio, cuerpo.AEntrada(), clsConcurrenciaHttp.VersionDe(HttpContext), cancellationToken);
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
    /// contra los catálogos de Nexo (nada queda guardado). <c>Idempotency-Key</c> opcional (#173): la repetición devuelve la misma
    /// cotización; si la primera falló después de confirmar el folio, 409 <c>ESTADO_NO_PERMITE</c> (<c>CAPTURA_EN_CURSO</c>) sin volver a
    /// llamar a Nexo. Si la lectura secundaria del resumen falla después de guardar, igual responde 201 con <c>avisos = [RESUMEN_INCOMPLETO]</c>.
    /// </summary>
    [HttpPost("solicitudes-costeo")]
    [ProducesResponseType<clsCotizacionResumen>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Enviar([FromBody] clsSolicitudCosteoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        clsCamposRetirados.Registrar(logger, "POST /web/solicitudes-costeo", cuerpo.CamposRetirados());
        var entrada = cuerpo.AEntrada();
        if (entrada.EsFallo)
        {
            return Error(entrada.Error);
        }

        // #173: con Idempotency-Key, un reintento del mismo POST (timeout, 500) devuelve la cotización de la primera sin otro folio
        // ni otra solicitud en Nexo. El resumen se arma tolerando fallos de la lectura secundaria: lo esencial ya quedó guardado.
        var clave = Request.Headers[EncabezadoIdempotencia].ToString();
        var resultado = await envio.CapturarUnaVezAsync(UsuarioId, entrada.Valor, clave.Length == 0 ? null : clave, cancellationToken);
        return resultado.EsFallo
            ? Error(resultado.Error)
            : StatusCode(StatusCodes.Status201Created, (await misCotizaciones.ResumirAsync(resultado.Valor, CancellationToken.None)) with
            {
                // HU-023.3: el resultado del cubicador por ruta y sus avisos (estos no se guardan: solo viajan en esta respuesta).
                Cubicacion = clsCubicacionRutaResumen.De(resultado.Valor),
            });
    }

    /// <summary>Encabezado de la clave de idempotencia (ADR-018 §8).</summary>
    public const string EncabezadoIdempotencia = "Idempotency-Key";

    /// <summary>
    /// HU-013.4 (ADR-018 §5), "Cotizar costo": 200 <c>CotizacionResumen</c> si se reenvió en el mismo folio o 201 con <c>Location</c> si
    /// se creó la versión <c>-vN</c> (los dos con el <c>ETag</c> de la cotización resultante); 400 <c>SOLICITUD_COSTEO_INVALIDA</c> o
    /// <c>ErrorWeb</c> (<c>Idempotency-Key</c> fuera de largo); 404; 409 <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (la misma clave con otro
    /// cuerpo, D4), <c>ESTADO_NO_PERMITE</c>, <c>PDF_YA_ENVIADO</c> o
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

        // HU-021.9 (#136): la respuesta trae siempre los conexos que se conservan (lista vacía si no había); conexos_descartados (#124)
        // queda vacío por compatibilidad: con la decisión 3 la re-solicitud ya no descarta conexos.
        var resumen = valor.Cotizacion with
        {
            ConexosDescartados = valor.ConexosDescartados ?? [],
            ConexosConservados = valor.ConexosConservados ?? [],
            // HU-028.1 (#183): solo si la cotización de origen tenía camiones rechazados por la Mesa de Costeo.
            AvisoRechazos = valor.AvisoRechazos,
        };
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
