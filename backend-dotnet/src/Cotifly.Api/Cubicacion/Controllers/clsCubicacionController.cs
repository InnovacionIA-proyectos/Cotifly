using Cotifly.Api.Comun;
using Cotifly.Api.Cubicacion.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Cubicacion.Controllers;

/// <summary>
/// "Calcular cubicaje" del formulario (HU-023.2, #157): vista previa contra el cubicador, sin persistencia. Dado un conjunto de bultos
/// con su tipo de mercancía, devuelve la lista de vehículos que recomienda el cubicador.
/// </summary>
[Route("web/cubicacion")]
public sealed class clsCubicacionController(IClsContextoUsuario usuario, IClsCalculoCubicajeService calculo) : clsControladorWebBase(usuario)
{
    public const string MensajeItemsRequeridos = "Debes indicar al menos un ítem para cubicar.";

    /// <summary>
    /// 200 <c>ResultadoCubicacion</c> (lista de vehículos, resultado, motivo, condicionada, advertencias y extradimensión); 400 si falta
    /// <c>items</c> (<c>ITEMS_REQUERIDOS</c>), si una mercancía no se reconoce (<c>MERCANCIA_DESCONOCIDA</c>) o si el cubicador rechaza la
    /// carga (<c>MERCANCIA_NO_CUBICABLE</c>, <c>DIMENSIONES_INVALIDAS</c>, <c>MERCANCIAS_REQUERIDAS</c>); 503 si el cubicador no está
    /// configurado o no responde (<c>CUBICADOR_NO_DISPONIBLE</c>), rechazó la llave (<c>CUBICADOR_NO_AUTORIZADO</c>) o respondió fuera
    /// del contrato (<c>CUBICADOR_RESPUESTA_INVALIDA</c>).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<clsResultadoCubicacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Cubicar([FromBody] clsCubicacionWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        if (cuerpo.Items is not { Count: > 0 } items)
        {
            return Error(new clsErrorDominio(clsCodigosError.ItemsRequeridos, MensajeItemsRequeridos));
        }

        var resultado = await calculo.CalcularAsync(UsuarioId, items, cuerpo.ClaseViaje, cancellationToken);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }
}
