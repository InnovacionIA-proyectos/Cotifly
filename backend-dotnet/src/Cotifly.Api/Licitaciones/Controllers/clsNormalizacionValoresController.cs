using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// Normalización de los textos del Excel de licitación con NEXO (ADR-028 y ADR-029). El front no puede llamar a NEXO
/// (CORS e identidad por header): Cotifly es el intermediario, con la identidad del comercial. Rol Comercial (política de la base).
/// </summary>
[Route("web/licitaciones")]
public sealed class clsNormalizacionValoresController(IClsContextoUsuario usuario, IClsNormalizacionValoresService normalizacion)
    : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>NormalizacionValores</c> (también sin IA en NEXO: <c>ia_usada = false</c> con lo que resolvieron las reglas); 400
    /// <c>VALORES_REQUERIDOS</c>, <c>DEMASIADOS_VALORES</c>, <c>CAMPO_INVALIDO</c>, <c>VALOR_DEMASIADO_LARGO</c>, <c>CLIENTE_NIT_INVALIDO</c>;
    /// 403 <c>NEXO_SIN_PERMISO</c>; 429 <c>IA_TOPE_DIARIO</c>; 503 <c>NEXO_NO_DISPONIBLE</c>.
    /// </summary>
    [HttpPost("normalizar-valores")]
    [ProducesResponseType<clsNormalizacionValores>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Normalizar([FromBody] clsNormalizacionValoresWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await normalizacion.NormalizarAsync(UsuarioId, cuerpo.AEntrada(), cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }
}
