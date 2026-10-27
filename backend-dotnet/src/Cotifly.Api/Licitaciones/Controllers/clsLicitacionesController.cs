using System.Text.Json;
using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// Licitaciones desde Excel (HU-011, ADR-017, <c>specs/openapi.yaml</c>): analizar el pliego en Nexo, crear la licitación con sus
/// líneas mapeadas y verla. El cuerpo se lee a mano (multipart o JSON) para que la sesión se valide antes que el tipo de contenido
/// y el límite del cuerpo sea el de <c>SeguridadHttp:LimiteCuerpoLicitacionBytes</c>. Solo cotizaciones propias.
/// </summary>
[Route("web")]
public sealed class clsLicitacionesController(
    IClsContextoUsuario usuario,
    IClsLicitacionService licitaciones,
    IClsCubicacionLicitacionService cubicacion,
    ILogger<clsLicitacionesController> logger) : clsControladorWebBase(usuario)
{
    public const string ParteArchivo = "archivo";
    public const string ParteLicitacion = "licitacion";

    /// <summary>200 <c>AnalisisExcel</c>; 400 <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c>, <c>ARCHIVO_DEMASIADO_GRANDE</c>, <c>EXCEL_*</c>; 503 sin Nexo.</summary>
    [HttpPost("licitaciones/analizar-excel")]
    [ProducesResponseType<clsAnalisisExcel>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AnalizarExcel(CancellationToken cancellationToken)
    {
        var archivo = Request.HasFormContentType ? await clsArchivoMultipart.LeerParteAsync(Request, ParteArchivo, cancellationToken) : null;
        var resultado = await licitaciones.AnalizarAsync(UsuarioId, archivo ?? new clsArchivoSubido(null, []), cancellationToken);
        return resultado.Coincidir<IActionResult>(a => Ok(a), Error);
    }

    /// <summary>
    /// 201 <c>Licitacion</c> aunque Nexo falle (queda en <c>error_envio</c>); 400 <c>ReporteLicitacionInvalida</c> con todas las filas
    /// que fallaron, o <c>ErrorNegocio</c> (archivo, línea de negocio, folio). Multipart (<c>licitacion</c> JSON + <c>archivo</c>) o JSON.
    /// </summary>
    [HttpPost("licitaciones")]
    [ProducesResponseType<clsLicitacion>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsReporteLicitacionWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Crear(CancellationToken cancellationToken)
    {
        clsLicitacionWebRequest? cuerpo;
        clsArchivoSubido? archivo = null;
        try
        {
            if (Request.HasFormContentType)
            {
                var formulario = await Request.ReadFormAsync(cancellationToken);
                var json = formulario[ParteLicitacion].ToString();
                cuerpo = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<clsLicitacionWebRequest>(json, clsJsonWeb.Opciones);
                if (formulario.Files.GetFile(ParteArchivo) is { } subido)
                {
                    archivo = await clsArchivoMultipart.LeerAsync(subido, cancellationToken);
                }
            }
            else
            {
                cuerpo = await JsonSerializer.DeserializeAsync<clsLicitacionWebRequest>(Request.Body, clsJsonWeb.Opciones, cancellationToken);
            }
        }
        catch (JsonException)
        {
            cuerpo = null;
        }
        catch (InvalidDataException)
        {
            cuerpo = null;
        }

        if (cuerpo is null)
        {
            return BadRequest(new clsErrorWeb(clsErrorWeb.MensajeCuerpoInvalido));
        }

        if (archivo is { Contenido.Length: > clsArchivoLicitacion.TamanoMaximoBytes })
        {
            return Error(new clsErrorDominio(clsCodigosError.ArchivoDemasiadoGrande, "El archivo supera el máximo de 9,9 MB (10 MB con el envío)."));
        }

        clsCamposRetirados.Registrar(logger, "POST /web/licitaciones", cuerpo.CamposRetirados());
        var resultado = await licitaciones.CrearAsync(UsuarioId, cuerpo.AEntrada(archivo), cancellationToken);
        if (resultado.Errores.Count > 0 && resultado.Error is { } invalida)
        {
            return BadRequest(new clsReporteLicitacionWeb(clsErrorNegocioWeb.Desde(invalida).Error, resultado.Errores));
        }

        if (resultado.Error is { } error)
        {
            return Error(error);
        }

        var vista = await licitaciones.ObtenerAsync(UsuarioId, resultado.Cotizacion!.Folio, CancellationToken.None);
        return vista.Coincidir<IActionResult>(v => StatusCode(StatusCodes.Status201Created, v), Error);
    }

    /// <summary>
    /// 200 <c>Licitacion</c> con el estado y el precio de cada línea (nunca el costo de Nexo), sus camiones y el progreso de la cubicación en
    /// segundo plano (HU-023.5: <c>progreso_cubicacion</c>, para consultarlo cada pocos segundos mientras esté <c>en_curso</c>); 404 si no es suya.
    /// </summary>
    [HttpGet("licitaciones/{folio}")]
    [ProducesResponseType<clsLicitacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(string folio, CancellationToken cancellationToken) =>
        (await licitaciones.ObtenerAsync(UsuarioId, folio, cancellationToken)).Coincidir<IActionResult>(l => Ok(l), Error);

    /// <summary>
    /// HU-023.5 (#160): reintenta las rutas con la cubicación fallida (vuelven a la cola) o, con <c>usar_respaldo</c>, las acepta con un
    /// camión sin vehículo (lo resuelve la Mesa de Costeo) y, si no queda nada pendiente, envía la licitación a Nexo. 200 <c>Licitacion</c>;
    /// 404 si no es una licitación suya; 409 <c>ESTADO_NO_PERMITE</c> si no tiene rutas fallidas.
    /// </summary>
    [HttpPost("licitaciones/{folio}/cubicacion/reintentar")]
    [ProducesResponseType<clsLicitacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReintentarCubicacion(string folio, CancellationToken cancellationToken)
    {
        clsReintentoCubicacionWebRequest? cuerpo = null;
        if (Request.ContentLength is > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                cuerpo = await JsonSerializer.DeserializeAsync<clsReintentoCubicacionWebRequest>(Request.Body, clsJsonWeb.Opciones, cancellationToken);
            }
            catch (JsonException)
            {
                return BadRequest(new clsErrorWeb(clsErrorWeb.MensajeCuerpoInvalido));
            }
        }

        var resultado = await cubicacion.ReintentarAsync(UsuarioId, folio, cuerpo?.UsarRespaldo ?? false, cancellationToken);
        if (resultado.EsFallo)
        {
            return Error(resultado.Error);
        }

        var vista = await licitaciones.ObtenerAsync(UsuarioId, resultado.Valor.Folio, CancellationToken.None);
        return vista.Coincidir<IActionResult>(v => Ok(v), Error);
    }

    /// <summary>
    /// 200 xlsx: el Excel original del cliente con las columnas de precio final, estado, servicios conexos y camiones (HU-011.3, HU-023.5),
    /// más las hojas "Resumen Cotifly" y "Camiones Cotifly"; sin original, una hoja con los campos mapeados. Nunca trae costos de Nexo. 404
    /// si no es suya o no es licitación.
    /// </summary>
    [HttpGet("licitaciones/{folio}/excel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Exportar(string folio, CancellationToken cancellationToken)
    {
        var resultado = await licitaciones.ExportarAsync(UsuarioId, folio, cancellationToken);
        return resultado.Coincidir<IActionResult>(a => File(a.Contenido, a.TipoContenido, a.Nombre), Error);
    }
}
