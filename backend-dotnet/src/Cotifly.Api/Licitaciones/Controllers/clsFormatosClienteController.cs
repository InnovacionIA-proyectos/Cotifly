using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// HU-044.5 (#256, ADR-028, <c>specs/openapi.yaml</c>): lo que NEXO recuerda de cada cliente para leer su Excel de licitación. Las
/// <b>plantillas de mapeo</b> (con plantilla, la siguiente carga del mismo formato no llama a la IA) y los <b>formatos de lectura</b> o
/// recetas (con un formato guardado, el siguiente Excel parecido se lee sin IA). Cotifly es solo el puente: no guarda copia. Los listados
/// exigen el NIT; "no encontrado" es 404 con el código de NEXO. Rol Comercial (<see cref="clsControladorWebBase"/>).
/// </summary>
[Route("web/licitaciones")]
public sealed class clsFormatosClienteController(IClsContextoUsuario usuario, IClsFormatosClienteService formatos)
    : clsControladorWebBase(usuario)
{
    public const string ParteArchivo = "archivo";
    public const string ParteReceta = "receta";
    public const string ParteNitCliente = "nit_cliente";
    public const string ParteNombre = "nombre";
    public const string ParteInstrucciones = "instrucciones";

    /// <summary>
    /// 201 <c>PlantillaMapeo</c> la primera vez, 200 si reemplaza a la del cliente con esos encabezados; 400 <c>CLIENTE_REQUERIDO</c> o
    /// <c>CLIENTE_NIT_INVALIDO</c> (de Cotifly o de NEXO) y los rechazos de NEXO con su código; 403 <c>NEXO_SIN_PERMISO</c>; 503 sin NEXO.
    /// </summary>
    [HttpPut("plantillas-mapeo")]
    [ProducesResponseType<clsPlantillaMapeoWeb>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsPlantillaMapeoWeb>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GuardarPlantilla([FromBody] clsPlantillaMapeoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await formatos.GuardarPlantillaAsync(UsuarioId, cuerpo.APeticion(), cancellationToken);
        return resultado.Coincidir<IActionResult>(
            g => StatusCode(g.Creada ? StatusCodes.Status201Created : StatusCodes.Status200OK, clsPlantillaMapeoWeb.Desde(g.Plantilla)), Error);
    }

    /// <summary>200 lista de <c>PlantillaMapeo</c> del cliente (vacía si no tiene); 400 <c>CLIENTE_REQUERIDO</c> sin NIT, sin llamar a NEXO.</summary>
    [HttpGet("plantillas-mapeo")]
    [ProducesResponseType<IReadOnlyList<clsPlantillaMapeoWeb>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListarPlantillas([FromQuery(Name = "nit_cliente")] string? nitCliente, CancellationToken cancellationToken) =>
        (await formatos.ListarPlantillasAsync(UsuarioId, nitCliente, cancellationToken))
            .Coincidir<IActionResult>(l => Ok(l.Select(clsPlantillaMapeoWeb.Desde).ToList()), Error);

    /// <summary>
    /// 204; 404 <c>PLANTILLA_NO_ENCONTRADA</c> (un id que no es un GUID no llega a NEXO); 400 <c>COSTEO_NEXO_RECHAZADA</c> si NEXO responde otro
    /// 4xx o un 404 sin código; 403 <c>NEXO_SIN_PERMISO</c>.
    /// </summary>
    [HttpDelete("plantillas-mapeo/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EliminarPlantilla(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var plantillaId))
        {
            return Error(new clsErrorDominio(clsCodigosError.PlantillaNoEncontrada, "La plantilla no existe."));
        }

        return (await formatos.EliminarPlantillaAsync(UsuarioId, plantillaId, cancellationToken)).Coincidir<IActionResult>(_ => NoContent(), Error);
    }

    /// <summary>
    /// 201 <c>RecetaLectura</c> la primera vez, 200 si reemplaza a un formato parecido del cliente; 400 <c>ARCHIVO_REQUERIDO</c>,
    /// <c>FORMATO_NO_SOPORTADO</c>, <c>ARCHIVO_DEMASIADO_GRANDE</c>, <c>RECETA_REQUERIDA</c> o <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO, o el
    /// código de NEXO (<c>RECETA_INVALIDA</c>, <c>RECETA_SIN_FILAS</c>, <c>CLIENTE_NIT_INVALIDO</c>...); 403 <c>NEXO_SIN_PERMISO</c>; 503 sin
    /// NEXO. Multipart con <c>archivo</c>, <c>receta</c> (texto JSON que llega a NEXO tal cual), <c>nit_cliente</c>, <c>nombre</c> e
    /// <c>instrucciones</c>. Nunca se reintenta.
    /// </summary>
    [HttpPut("recetas-lectura")]
    [ProducesResponseType<clsRecetaLecturaWeb>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsRecetaLecturaWeb>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GuardarReceta(CancellationToken cancellationToken)
    {
        clsArchivoSubido? archivo = null;
        string? receta = null, nit = null, nombre = null, instrucciones = null;
        if (Request.HasFormContentType)
        {
            try
            {
                var formulario = await Request.ReadFormAsync(cancellationToken);
                if (formulario.Files.GetFile(ParteArchivo) is { } subido)
                {
                    archivo = await clsArchivoMultipart.LeerAsync(subido, cancellationToken);
                }

                receta = Texto(formulario, ParteReceta);
                nit = Texto(formulario, ParteNitCliente);
                nombre = Texto(formulario, ParteNombre);
                instrucciones = Texto(formulario, ParteInstrucciones);
            }
            catch (InvalidDataException)
            {
                return BadRequest(new clsErrorWeb(clsErrorWeb.MensajeCuerpoInvalido));
            }
        }

        var entrada = new clsEntradaGuardarReceta(archivo ?? new clsArchivoSubido(null, []), receta, nit, nombre, instrucciones);
        var resultado = await formatos.GuardarRecetaAsync(UsuarioId, entrada, cancellationToken);
        return resultado.Coincidir<IActionResult>(
            g => StatusCode(g.Creada ? StatusCodes.Status201Created : StatusCodes.Status200OK, clsRecetaLecturaWeb.Desde(g.Receta)), Error);
    }

    /// <summary>200 lista de <c>RecetaLectura</c> del cliente (vacía si no tiene); 400 <c>CLIENTE_REQUERIDO</c> sin NIT, sin llamar a NEXO.</summary>
    [HttpGet("recetas-lectura")]
    [ProducesResponseType<IReadOnlyList<clsRecetaLecturaWeb>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListarRecetas([FromQuery(Name = "nit_cliente")] string? nitCliente, CancellationToken cancellationToken) =>
        (await formatos.ListarRecetasAsync(UsuarioId, nitCliente, cancellationToken))
            .Coincidir<IActionResult>(l => Ok(l.Select(clsRecetaLecturaWeb.Desde).ToList()), Error);

    /// <summary>
    /// 204; 404 <c>RECETA_NO_ENCONTRADA</c> (un id que no es un GUID no llega a NEXO); 400 <c>COSTEO_NEXO_RECHAZADA</c> si NEXO responde otro 4xx
    /// o un 404 sin código; 403 <c>NEXO_SIN_PERMISO</c>.
    /// </summary>
    [HttpDelete("recetas-lectura/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EliminarReceta(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var recetaId))
        {
            return Error(new clsErrorDominio(clsCodigosError.RecetaNoEncontrada, "El formato no existe."));
        }

        return (await formatos.EliminarRecetaAsync(UsuarioId, recetaId, cancellationToken)).Coincidir<IActionResult>(_ => NoContent(), Error);
    }

    /// <summary>El valor de un campo del formulario tal cual (sin recortar: la receta llega a NEXO exacta), o <c>null</c> si no vino.</summary>
    private static string? Texto(IFormCollection formulario, string campo) =>
        formulario.TryGetValue(campo, out var valor) && valor.Count > 0 ? valor.ToString() : null;
}
