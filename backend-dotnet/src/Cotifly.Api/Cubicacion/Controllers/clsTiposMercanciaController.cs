using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Cubicacion.Controllers;

/// <summary>Catálogo de tipos de mercancía del cubicador para el desplegable de mercancías del formulario de captura (HU-021.5, #133).</summary>
[Route("web/tipos-mercancia")]
public sealed class clsTiposMercanciaController(IClsContextoUsuario usuario, IClsCubicador cubicador) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 el catálogo completo (<c>[{id, nombre}]</c>, ordenado por nombre; con la caché de <c>Cubicador:MinutosCacheCatalogos</c>); 503
    /// <c>CUBICADOR_NO_DISPONIBLE</c> si el cubicador no está configurado o no responde.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<clsTipoMercanciaCubicador>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        var resultado = await cubicador.ListarTiposMercanciaAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(t => Ok(t), Error);
    }
}
