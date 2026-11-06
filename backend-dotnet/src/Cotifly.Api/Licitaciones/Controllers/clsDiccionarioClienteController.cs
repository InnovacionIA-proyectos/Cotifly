using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// HU-044.5 (#256, ADR-028): consulta del diccionario del cliente de NEXO (los términos que NEXO usa para proponer el mapeo y normalizar los
/// valores del Excel). Solo lectura y, mientras NEXO no filtre por cliente, solo los términos generales. La escritura
/// (<c>POST/PUT/DELETE</c>) espera el contrato §3.19.1 de NEXO (S6-HU-34.10). Rol Comercial (<see cref="clsControladorWebBase"/>).
/// </summary>
[Route("web/diccionario-cliente")]
public sealed class clsDiccionarioClienteController(IClsContextoUsuario usuario, IClsFormatosClienteService formatos)
    : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>DiccionarioCliente</c>: solo los términos generales; con <c>nit_cliente</c>, además el aviso
    /// <c>TERMINOS_CLIENTE_NO_DISPONIBLES</c>. 400 <c>TIPO_TERMINO_INVALIDO</c> o <c>CLIENTE_NIT_INVALIDO</c>; 403 <c>NEXO_SIN_PERMISO</c>;
    /// 503 sin NEXO.
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

    /// <summary>200 <c>OpcionesDiccionarioCliente</c> (tipos, campos de NEXO, unidades y contextos); 503 sin NEXO.</summary>
    [HttpGet("opciones")]
    [ProducesResponseType<clsOpcionesDiccionarioCliente>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Opciones(CancellationToken cancellationToken) =>
        (await formatos.ObtenerOpcionesDiccionarioAsync(UsuarioId, cancellationToken)).Coincidir<IActionResult>(o => Ok(o), Error);
}
