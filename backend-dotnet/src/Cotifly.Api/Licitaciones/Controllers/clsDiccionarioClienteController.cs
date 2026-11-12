using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// Diccionario del cliente en NEXO (ADR-028): los términos que NEXO usa para proponer el mapeo y normalizar los valores del Excel. Consultar
/// lo pueden los tres roles de la consola (<see cref="clsControladorWebBase"/>); crear, editar y borrar, solo el rol activo <c>comercial</c>
/// (<see cref="clsPoliticas.SoloComercial"/>) y siempre para un cliente: los términos generales no se tocan desde Cotifly.
/// </summary>
[Route("web/diccionario-cliente")]
public sealed class clsDiccionarioClienteController(IClsContextoUsuario usuario, IClsFormatosClienteService formatos)
    : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>DiccionarioCliente</c>: con <c>nit_cliente</c>, los términos del cliente y los generales; sin él, solo los generales.
    /// 400 <c>TIPO_TERMINO_INVALIDO</c> o <c>CLIENTE_NIT_INVALIDO</c>; 403 <c>NEXO_SIN_PERMISO</c>; 503 sin NEXO.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<clsDiccionarioClienteWeb>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Consultar(
        [FromQuery(Name = "nit_cliente")] string? nitCliente, [FromQuery] string? tipo, [FromQuery] string? q, CancellationToken cancellationToken) =>
        (await formatos.ConsultarDiccionarioAsync(UsuarioId, nitCliente, tipo, q, cancellationToken))
            .Coincidir<IActionResult>(c => Ok(clsDiccionarioClienteWeb.Desde(c)), Error);

    /// <summary>
    /// 200 <c>OpcionesDiccionarioCliente</c> (tipos, campos de NEXO, unidades y contextos); 400 <c>COSTEO_NEXO_RECHAZADA</c> si NEXO responde un
    /// 4xx; 403 <c>NEXO_SIN_PERMISO</c>; 503 sin NEXO.
    /// </summary>
    [HttpGet("opciones")]
    [ProducesResponseType<clsOpcionesDiccionarioCliente>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Opciones(CancellationToken cancellationToken) =>
        (await formatos.ObtenerOpcionesDiccionarioAsync(UsuarioId, cancellationToken)).Coincidir<IActionResult>(o => Ok(o), Error);

    /// <summary>
    /// 201 <c>TerminoDiccionario</c> creado para el cliente de <c>nit_cliente</c>. 400 con los códigos de validación o de NEXO (o
    /// <c>ErrorWeb</c> por largos de más); 403 <c>ErrorWeb</c> si el rol activo no es <c>comercial</c>, <c>NEXO_SIN_PERMISO</c>; 409
    /// <c>TERMINO_DUPLICADO</c>; 503 sin NEXO.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = clsPoliticas.SoloComercial)]
    [ProducesResponseType<clsTerminoDiccionarioWeb>(StatusCodes.Status201Created)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Crear([FromBody] clsTerminoDiccionarioWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        return (await formatos.CrearTerminoAsync(UsuarioId, cuerpo.AEntrada(), cancellationToken))
            .Coincidir<IActionResult>(t => StatusCode(StatusCodes.Status201Created, clsTerminoDiccionarioWeb.Desde(t)), Error);
    }

    /// <summary>
    /// 200 <c>TerminoDiccionario</c> editado. Además de los errores de <see cref="Crear"/>: 403 <c>TERMINO_GENERAL_NO_EDITABLE</c> y 404
    /// <c>TERMINO_NO_ENCONTRADO</c> (no existe o no es del cliente de <c>nit_cliente</c>).
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = clsPoliticas.SoloComercial)]
    [ProducesResponseType<clsTerminoDiccionarioWeb>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Editar(string id, [FromBody] clsTerminoDiccionarioWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        if (!Guid.TryParse(id, out var terminoId))
        {
            return NoEncontrado();
        }

        return (await formatos.EditarTerminoAsync(UsuarioId, terminoId, cuerpo.AEntrada(), cancellationToken))
            .Coincidir<IActionResult>(t => Ok(clsTerminoDiccionarioWeb.Desde(t)), Error);
    }

    /// <summary>
    /// 204 borrado. 400 <c>CLIENTE_REQUERIDO</c> sin <c>nit_cliente</c>; 403 <c>ErrorWeb</c> (rol), <c>TERMINO_GENERAL_NO_EDITABLE</c> o
    /// <c>NEXO_SIN_PERMISO</c>; 404 <c>TERMINO_NO_ENCONTRADO</c>; 503 sin NEXO.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = clsPoliticas.SoloComercial)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Eliminar(string id, [FromQuery(Name = "nit_cliente")] string? nitCliente, CancellationToken cancellationToken) =>
        !Guid.TryParse(id, out var terminoId)
            ? NoEncontrado()
            : (await formatos.EliminarTerminoAsync(UsuarioId, terminoId, nitCliente, cancellationToken)).Coincidir<IActionResult>(_ => NoContent(), Error);

    /// <summary>Un id que no es un GUID no puede ser un término de NEXO: 404 <c>TERMINO_NO_ENCONTRADO</c> sin llamar a NEXO.</summary>
    private static IActionResult NoEncontrado() =>
        Error(new clsErrorDominio(clsCodigosError.TerminoNoEncontrado, clsFormatosClienteService.MensajeTerminoNoEncontrado));
}
