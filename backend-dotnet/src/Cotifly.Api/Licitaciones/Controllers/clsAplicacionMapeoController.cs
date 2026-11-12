using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Licitaciones.Controllers;

/// <summary>
/// Aplica en NEXO el mapeo confirmado a todas las filas del Excel de licitación (ADR-028 y ADR-029) y devuelve las líneas con
/// los nombres de <c>LineaLicitacionWeb</c> (más <c>fila_excel</c> y <c>linea_nexo</c>), listas para <c>POST /web/licitaciones</c>. El front no
/// puede llamar a NEXO (CORS e identidad por header): Cotifly es el intermediario, con la identidad del comercial. Rol Comercial (política de
/// la base).
/// </summary>
[Route("web/licitaciones")]
public sealed class clsAplicacionMapeoController(IClsContextoUsuario usuario, IClsAplicacionMapeoService mapeo)
    : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 <c>MapeoAplicado</c> (con los errores por fila en <c>errores[]</c>); 400 por la forma de la petición (<c>MAPEO_REQUERIDO</c>,
    /// <c>DEMASIADAS_COLUMNAS</c>, <c>COLUMNA_INVALIDA</c>, <c>COLUMNA_FUERA_DE_RANGO</c>, <c>CAMPO_INVALIDO</c>, <c>CAMPO_DUPLICADO</c>,
    /// <c>TRANSFORMACION_INVALIDA</c>, <c>EXCEL_SIN_DATOS</c>, <c>DEMASIADAS_FILAS</c>, <c>FILA_INVALIDA</c>, <c>CLIENTE_NIT_INVALIDO</c>) o
    /// por el mapeo en NEXO (su código: <c>CAMPO_REQUERIDO</c>, <c>TRANSFORMACION_NO_APLICA</c>...); 403 <c>NEXO_SIN_PERMISO</c>; 429
    /// <c>IA_TOPE_DIARIO</c>; 503 <c>NEXO_NO_DISPONIBLE</c>.
    /// </summary>
    [HttpPost("aplicar-mapeo")]
    [ProducesResponseType<clsMapeoAplicado>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Aplicar([FromBody] clsAplicarMapeoWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        var resultado = await mapeo.AplicarAsync(UsuarioId, cuerpo.AEntrada(), cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }
}
