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
    /// 200 catálogo completo de municipios (sin paginar); 400 si el catálogo de Nexo no está disponible
    /// (<c>MUNICIPIOS_NO_DISPONIBLE</c>).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<clsMunicipio>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        var resultado = await municipios.ObtenerAsync(UsuarioId, cancellationToken);
        // Contrato vigente de GET /web/municipios: MUNICIPIOS_NO_DISPONIBLE es 400 aquí (en las licitaciones es 503, HU-013.11).
        // Pendiente de alinear a 503 en una issue propia (ADR-018 §16).
        return resultado.Coincidir<IActionResult>(m => Ok(m), e => BadRequest(clsErrorNegocioWeb.Desde(e)));
    }
}
