using Cotifly.Api.Comun;
using Cotifly.Api.Cubicacion.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Cubicacion.Controllers;

/// <summary>
/// Cubicación rápida (sin persistencia) para el formulario de captura: dado un conjunto de bultos, sugiere el mejor
/// vehículo y, si hace falta más de uno, la flota necesaria.
/// </summary>
[Route("web/cubicacion")]
public sealed class clsCubicacionController(IClsContextoUsuario usuario, IClsCubicacionProvider cubicacion) : clsControladorWebBase(usuario)
{
    public const string MensajeItemsRequeridos = "Debes indicar al menos un ítem para cubicar.";

    /// <summary>
    /// 200 <c>ResultadoCubicacion</c> (mejor vehículo, flota si hace falta, advertencias y detección de
    /// extradimensión); 400 si falta <c>items</c> (<c>ITEMS_REQUERIDOS</c>), si Nexo/Cubicador rechaza la solicitud
    /// (<c>CUBICACION_DEMASIADO_GRANDE</c>, típicamente por superar el límite de bultos por solicitud) o si la
    /// cubicación de Nexo no está disponible (<c>CUBICACION_NO_DISPONIBLE</c>).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<clsResultadoCubicacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Cubicar([FromBody] clsCubicacionWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        if (cuerpo.Items is not { Count: > 0 } items)
        {
            return Error(new clsErrorDominio(clsCodigosError.ItemsRequeridos, MensajeItemsRequeridos));
        }

        var resultado = await cubicacion.CubicarAsync(UsuarioId, items, cuerpo.ClaseViaje, cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }
}
