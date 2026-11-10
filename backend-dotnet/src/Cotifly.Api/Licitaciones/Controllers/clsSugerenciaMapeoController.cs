using System.Globalization;
using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// HU-044.2 (#253, ADR-028, <c>specs/openapi.yaml</c>): la importación inteligente del Excel de licitación con NEXO. <c>sugerir-mapeo</c>
/// propone el mapeo de las columnas de una hoja y <c>leer-estructura</c> lee con IA un Excel de formato complejo y devuelve tablas con la
/// forma de <c>analizar-excel</c>. Cotifly es solo el puente: no guarda nada. Rol Comercial (<see cref="clsControladorWebBase"/>).
/// </summary>
[Route("web")]
public sealed class clsSugerenciaMapeoController(IClsContextoUsuario usuario, IClsSugerenciaMapeoService sugerencias)
    : clsControladorWebBase(usuario)
{
    public const string ParteArchivo = "archivo";
    public const string ParteNitCliente = "nit_cliente";
    public const string ParteInstrucciones = "instrucciones";
    public const string ParteReceta = "receta";

    /// <summary>400 <c>ErrorWeb</c> de un formulario con una parte repetida (<c>{0}</c> = el nombre de la parte). El mismo de #256.</summary>
    public const string MensajeParteRepetida = "Formulario inválido: la parte '{0}' viene más de una vez.";

    /// <summary>
    /// 200 <c>SugerenciaMapeo</c>, también sin propuesta (<c>ia_usada = false</c>) con más de 100 columnas (<c>DEMASIADAS_COLUMNAS</c>), la
    /// importación apagada o NEXO caído (<c>NEXO_NO_DISPONIBLE</c>); 400 <c>ENCABEZADOS_REQUERIDOS</c>, <c>CLIENTE_NIT_INVALIDO</c> o un
    /// rechazo de NEXO; 403 <c>NEXO_SIN_PERMISO</c>; 429 <c>IA_TOPE_DIARIO</c>.
    /// </summary>
    [HttpPost("licitaciones/sugerir-mapeo")]
    [ProducesResponseType<clsSugerenciaMapeo>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Sugerir([FromBody] clsSugerirMapeoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await sugerencias.SugerirAsync(UsuarioId, cuerpo.APeticion(), cancellationToken);
        return resultado.Coincidir<IActionResult>(s => Ok(s), Error);
    }

    /// <summary>
    /// 200 <c>LecturaEstructura</c> (también con <c>ia_usada = false</c>); 400 <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c> o
    /// <c>ARCHIVO_DEMASIADO_GRANDE</c> (o <c>LECTURA_DEMASIADO_GRANDE</c> si no cabe con la receta y las instrucciones) sin llamar a NEXO, o el
    /// código de NEXO (<c>EXCEL_INVALIDO</c>, <c>RECETA_INVALIDA</c>, <c>VALIDATION_ERROR</c> con <c>detalle.campos</c>...); 403 <c>NEXO_SIN_PERMISO</c>; 429 <c>IA_TOPE_DIARIO</c>; 503 <c>NEXO_NO_DISPONIBLE</c>. Multipart con
    /// <c>archivo</c>, <c>nit_cliente</c>, <c>instrucciones</c> y <c>receta</c> (texto JSON que llega a NEXO tal cual).
    /// </summary>
    [HttpPost("licitaciones/leer-estructura")]
    [ProducesResponseType<clsLecturaEstructuraWeb>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LeerEstructura(CancellationToken cancellationToken)
    {
        clsArchivoSubido? archivo = null;
        string? nit = null, instrucciones = null, receta = null;
        if (Request.HasFormContentType)
        {
            try
            {
                var formulario = await Request.ReadFormAsync(cancellationToken);
                if (ParteRepetida(formulario) is { } repetida)
                {
                    return BadRequest(new clsErrorWeb(string.Format(CultureInfo.InvariantCulture, MensajeParteRepetida, repetida)));
                }

                if (formulario.Files.GetFile(ParteArchivo) is { } subido)
                {
                    archivo = await clsArchivoMultipart.LeerAsync(subido, cancellationToken);
                }

                nit = Texto(formulario, ParteNitCliente);
                instrucciones = Texto(formulario, ParteInstrucciones);
                receta = Texto(formulario, ParteReceta);
            }
            catch (InvalidDataException)
            {
                return BadRequest(new clsErrorWeb(clsErrorWeb.MensajeCuerpoInvalido));
            }
        }

        var entrada = new clsEntradaLeerEstructura(archivo ?? new clsArchivoSubido(null, []), nit, instrucciones, receta);
        var resultado = await sugerencias.LeerEstructuraAsync(UsuarioId, entrada, cancellationToken);
        return resultado.Coincidir<IActionResult>(l => Ok(clsLecturaEstructuraWeb.Desde(l)), Error);
    }

    /// <summary>
    /// D1 (validador de #253, igual que #256): la primera parte que vino más de una vez, o <c>null</c>. Dos partes con el mismo nombre se
    /// unirían con coma: dos NIT que la normalización funde en otro NIT, o una receta que deja de ser la que devolvió NEXO. No se adivina
    /// cuál vale: se rechaza el formulario sin llamar a NEXO.
    /// </summary>
    private static string? ParteRepetida(IFormCollection formulario)
    {
        if (formulario.Files.GetFiles(ParteArchivo).Count > 1)
        {
            return ParteArchivo;
        }

        return new[] { ParteNitCliente, ParteInstrucciones, ParteReceta }
            .FirstOrDefault(campo => formulario.TryGetValue(campo, out var valor) && valor.Count > 1);
    }

    /// <summary>El valor de un campo del formulario tal cual (sin recortar: la receta llega a NEXO exacta), o <c>null</c> si no vino.</summary>
    private static string? Texto(IFormCollection formulario, string campo) =>
        formulario.TryGetValue(campo, out var valor) && valor.Count > 0 ? valor.ToString() : null;
}
