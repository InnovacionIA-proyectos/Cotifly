using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Municipios.Controllers;

/// <summary>Catálogo de municipios para el selector de origen/destino del formulario de captura.</summary>
[Route("web/municipios")]
public sealed class clsMunicipiosController(IClsContextoUsuario usuario, IClsMunicipiosProvider municipios) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 municipios; 503 <c>MUNICIPIOS_NO_DISPONIBLE</c> si el catálogo de Nexo no responde (caído, 5xx, tiempo agotado, 429 o cupo
    /// bloqueado, sin configurar o el comercial sin <c>idUsuarioExterno</c>), igual que <c>GET /web/catalogos-flete</c> y que la
    /// captura y la licitación. Un error de validación del caso de uso sería 400 (traducción única de <c>clsRespuestasWeb</c>); hoy
    /// <c>q</c> y <c>departamento</c> son texto libre y se recortan a 100 caracteres, así que no hay entrada inválida. HU-023 (#176): con <c>q</c> o
    /// <c>departamento</c> es la búsqueda de Nexo <c>GET /api/captura-solicitudes/ciudades</c> (máximo 50, solo Colombia; de ahí salen
    /// los ids que viajan como <c>origen_municipio_id</c>/<c>destino_municipio_id</c>); sin filtros, el catálogo completo (sin paginar).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<clsMunicipio>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Listar([FromQuery] string? q, [FromQuery] string? departamento, CancellationToken cancellationToken)
    {
        var resultado = string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(departamento)
            ? await municipios.ObtenerAsync(UsuarioId, cancellationToken)
            : await municipios.BuscarAsync(UsuarioId, q, departamento, cancellationToken);
        // Alineado con la captura y la licitación (pendiente de #122): MUNICIPIOS_NO_DISPONIBLE es 503 también aquí.
        return resultado.Coincidir<IActionResult>(m => Ok(m), Error);
    }
}
