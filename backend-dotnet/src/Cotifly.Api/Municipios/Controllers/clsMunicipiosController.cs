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
    /// 200 municipios; 400 si el catálogo de Nexo no está disponible (<c>MUNICIPIOS_NO_DISPONIBLE</c>). HU-023 (#176): con <c>q</c> o
    /// <c>departamento</c> es la búsqueda de Nexo <c>GET /api/captura-solicitudes/ciudades</c> (máximo 50, solo Colombia; de ahí salen
    /// los ids que viajan como <c>origen_municipio_id</c>/<c>destino_municipio_id</c>); sin filtros, el catálogo completo (sin paginar).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<clsMunicipio>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar([FromQuery] string? q, [FromQuery] string? departamento, CancellationToken cancellationToken)
    {
        var resultado = string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(departamento)
            ? await municipios.ObtenerAsync(UsuarioId, cancellationToken)
            : await municipios.BuscarAsync(UsuarioId, q, departamento, cancellationToken);
        // Contrato vigente de GET /web/municipios: MUNICIPIOS_NO_DISPONIBLE es 400 aquí (en las licitaciones es 503, HU-013.11).
        // Pendiente de alinear a 503 en una issue propia (ADR-018 §16).
        return resultado.Coincidir<IActionResult>(m => Ok(m), e => BadRequest(clsErrorNegocioWeb.Desde(e)));
    }
}
