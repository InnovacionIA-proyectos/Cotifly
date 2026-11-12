using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Clientes.Controllers;

/// <summary>Datos de un cliente para el comercial (<c>specs/openapi.yaml</c>): buscador y destinatarios de envíos anteriores.</summary>
[Route("web/clientes")]
public sealed class clsClientesController(IClsContextoUsuario usuario, IClsDestinatariosPreviosService destinatarios, IClsBuscadorClientes buscador)
    : clsControladorWebBase(usuario)
{
    /// <summary>Largo máximo del NIT en la ruta (contrato).</summary>
    public const int LargoMaximoNit = 20;

    /// <summary>
    /// 200 <c>ResultadoBusquedaClientes</c>: el mismo catálogo de Nexo (clientes y prospectos) que consume el formulario de
    /// captura (ADR-005). 400 si falta <c>q</c>. 503 <c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c> si Nexo no está configurado
    /// (<c>Nexo:UrlBase</c> vacía) o no respondió, como los demás catálogos de Nexo.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<clsResultadoBusquedaClientes>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Buscar([FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new clsErrorWeb("Falta el texto de búsqueda (q)."));
        }

        var resultado = await buscador.BuscarAsync(UsuarioId, q.Trim(), cancellationToken);
        return resultado.Coincidir<IActionResult>(b => Ok(b), Error);
    }

    /// <summary>
    /// 200 <c>Destinatarios</c> del último envío exitoso del comercial a ese NIT (listas vacías si nunca le envió: nunca se ven
    /// envíos de otro comercial); 400 si el NIT supera 20 caracteres.
    /// </summary>
    [HttpGet("{nit}/destinatarios-previos")]
    [ProducesResponseType<clsDestinatarios>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DestinatariosPrevios(string nit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nit) || nit.Length > LargoMaximoNit)
        {
            return BadRequest(new clsErrorWeb($"El NIT debe tener entre 1 y {LargoMaximoNit} caracteres."));
        }

        return Ok(await destinatarios.ObtenerAsync(UsuarioId, nit, cancellationToken));
    }
}
