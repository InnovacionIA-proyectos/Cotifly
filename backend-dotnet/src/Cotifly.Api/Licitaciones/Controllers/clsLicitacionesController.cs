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
    IClsLicitacionService licitaciones) : clsControladorWebBase(usuario)
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
        var archivo = Request.HasFormContentType ? await LeerArchivoAsync(cancellationToken) : null;
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
                    archivo = await LeerAsync(subido, cancellationToken);
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
            return Error(new clsErrorDominio(clsCodigosError.ArchivoDemasiadoGrande, "El archivo supera 10 MB."));
        }

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

    /// <summary>200 <c>Licitacion</c> con el estado y el precio de cada línea (nunca el costo de Nexo); 404 si no es suya.</summary>
    [HttpGet("licitaciones/{folio}")]
    [ProducesResponseType<clsLicitacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(string folio, CancellationToken cancellationToken) =>
        (await licitaciones.ObtenerAsync(UsuarioId, folio, cancellationToken)).Coincidir<IActionResult>(l => Ok(l), Error);

    /// <summary>
    /// 200 xlsx: el Excel original del cliente con las columnas de precio final, estado y servicios conexos (HU-011.3), más la hoja
    /// "Resumen Cotifly"; sin original, una hoja con los campos mapeados. Nunca trae costos de Nexo. 404 si no es suya o no es licitación.
    /// </summary>
    [HttpGet("licitaciones/{folio}/excel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Exportar(string folio, CancellationToken cancellationToken)
    {
        var resultado = await licitaciones.ExportarAsync(UsuarioId, folio, cancellationToken);
        return resultado.Coincidir<IActionResult>(a => File(a.Contenido, a.TipoContenido, a.Nombre), Error);
    }

    private async Task<clsArchivoSubido?> LeerArchivoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var formulario = await Request.ReadFormAsync(cancellationToken);
            return formulario.Files.GetFile(ParteArchivo) is { } subido ? await LeerAsync(subido, cancellationToken) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Lee el archivo hasta 10 MB + 1 byte (lo que pase se reporta como <c>ARCHIVO_DEMASIADO_GRANDE</c>).</summary>
    private static async Task<clsArchivoSubido> LeerAsync(IFormFile subido, CancellationToken cancellationToken)
    {
        if (subido.Length > clsArchivoLicitacion.TamanoMaximoBytes)
        {
            return new clsArchivoSubido(subido.FileName, new byte[clsArchivoLicitacion.TamanoMaximoBytes + 1]);
        }

        using var memoria = new MemoryStream((int)Math.Max(subido.Length, 0));
        await using var flujo = subido.OpenReadStream();
        await flujo.CopyToAsync(memoria, cancellationToken);
        return new clsArchivoSubido(subido.FileName, memoria.ToArray());
    }
}
