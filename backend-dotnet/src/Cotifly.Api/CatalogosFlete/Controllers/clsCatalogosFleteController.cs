using Cotifly.Api.Comun;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.CatalogosFlete.Controllers;

/// <summary>Catálogos de tipo de vehículo, tipo de carrocería y clase de viaje para el formulario de captura.</summary>
[Route("web/catalogos-flete")]
public sealed class clsCatalogosFleteController(IClsContextoUsuario usuario, IClsCatalogosFleteProvider catalogos) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 los tres catálogos completos; 503 si los catálogos maestros de Nexo no están disponibles y no hay copia utilizable
    /// (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c>).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<clsCatalogosFlete>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Obtener(CancellationToken cancellationToken)
    {
        var resultado = await catalogos.ObtenerAsync(UsuarioId, cancellationToken);
        return resultado.Coincidir<IActionResult>(c => Ok(c), Error);
    }
}
