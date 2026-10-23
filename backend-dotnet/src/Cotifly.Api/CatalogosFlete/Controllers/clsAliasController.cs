using Cotifly.Api.Comun;
using Cotifly.Aplicacion.CatalogosFlete.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.CatalogosFlete.Controllers;

/// <summary>
/// HU-031.1 (#194): resolver un tipo de vehículo o una clase de contenedor escritos a mano contra Nexo (catálogo oficial y diccionario de
/// alias, NEXO #321). El front no puede llamar a Nexo directamente: el backend es el intermediario, como con los catálogos de flete.
/// </summary>
[Route("web/alias")]
public sealed class clsAliasController(IClsContextoUsuario usuario, IClsResolucionAliasService resolucion) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// <c>GET /web/alias/resolver?tipo=vehiculo|contenedor&amp;texto=TB&amp;texto=40HC</c>: uno o varios <c>texto</c> (hasta 200, en lote) en
    /// una sola llamada. 200 con un resultado por texto, en el mismo orden (<c>reconocido: false</c> si ni el catálogo ni el diccionario de
    /// Nexo lo conocen); 400 <c>SOLICITUD_INVALIDA</c> (tipo inválido, sin textos, más de 200 o uno de más de 100 caracteres); 503
    /// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> si hace falta el catálogo o el diccionario de Nexo y no hay una copia utilizable.
    /// </summary>
    [HttpGet("resolver")]
    [ProducesResponseType<clsAliasResueltos>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Resolver([FromQuery] string? tipo, [FromQuery] string?[]? texto, CancellationToken cancellationToken)
    {
        var resultado = await resolucion.ResolverAsync(UsuarioId, tipo, texto, cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }
}
