using System.Globalization;
using Cotifly.Api.Comun;
using Cotifly.Api.Cubicacion.DTOs;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Cubicacion.Controllers;

/// <summary>
/// "Calcular cubicaje" del formulario: vista previa contra el cubicador, sin persistencia en la base. Dado un conjunto de
/// bultos con su tipo de mercancía, devuelve la lista de vehículos que recomienda el cubicador. Si el
/// cubicador no responde dentro de <c>Cubicador:EsperaSincronaCubicacionSegundos</c>, responde 202 y el cálculo sigue en segundo plano
/// (<c>GET /web/cubicacion/{id}</c>).
/// </summary>
[Route("web/cubicacion")]
public sealed class clsCubicacionController(
    IClsContextoUsuario usuario, IClsCalculoCubicajeService calculo, IClsCubicacionRapidaService rapida) : clsControladorWebBase(usuario)
{
    public const string MensajeItemsRequeridos = "Debes indicar al menos un ítem para cubicar.";

    public const string MensajeNoEncontrada =
        "La cubicación no existe, ya venció o no es tuya: vuelve a calcular el cubicaje.";

    /// <summary>Segundos sugeridos entre consultas mientras la cubicación está en proceso (<c>Retry-After</c>).</summary>
    public const int SegundosConsulta = 2;

    /// <summary>
    /// 200 <c>ResultadoCubicacion</c> (lista de vehículos, resultado, motivo, condicionada, advertencias y extradimensión) si el cubicador
    /// responde a tiempo; 202 <c>CubicacionEnProceso</c> (<c>Location</c> y <c>Retry-After</c>) si sigue calculando: consultar
    /// <c>GET /web/cubicacion/{id}</c>. 400 si falta <c>items</c> (<c>ITEMS_REQUERIDOS</c>), si un ítem tiene medidas o peso fuera de rango
    /// (<c>DIMENSIONES_INVALIDAS</c>) o una cantidad fuera de rango (<c>POSICION_INVALIDA</c>), los dos con <c>detalle.item</c> y validados
    /// antes de llamar, si una mercancía no se reconoce (<c>MERCANCIA_DESCONOCIDA</c>) o si el cubicador rechaza la carga
    /// (<c>MERCANCIA_NO_CUBICABLE</c>, <c>DIMENSIONES_INVALIDAS</c>, <c>MERCANCIAS_REQUERIDAS</c>, o un 422: <c>CUBICADOR_PETICION_RECHAZADA</c>);
    /// 503 si el cubicador no está configurado o no responde (<c>CUBICADOR_NO_DISPONIBLE</c>), rechazó la llave
    /// (<c>CUBICADOR_NO_AUTORIZADO</c>), respondió fuera del contrato (<c>CUBICADOR_RESPUESTA_INVALIDA</c>) o está ocupado con otras cargas
    /// (<c>CUBICADOR_OCUPADO</c>, con <c>Retry-After</c>).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<clsResultadoCubicacion>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsCubicacionEnProcesoWeb>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Cubicar([FromBody] clsCubicacionWebRequest cuerpo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);
        if (cuerpo.Items is not { Count: > 0 } items)
        {
            return Error(new clsErrorDominio(clsCodigosError.ItemsRequeridos, MensajeItemsRequeridos));
        }

        if (calculo.Validar(items) is { } invalido)
        {
            return Error(invalido);
        }

        var inicio = await rapida.IniciarAsync(UsuarioId, items, cuerpo.ClaseViaje, cancellationToken);
        if (inicio.EsFallo)
        {
            return Error(inicio.Error);
        }

        if (inicio.Valor.Resultado is { } resultado)
        {
            return Ok(resultado);
        }

        var id = inicio.Valor.IdPendiente!.Value;
        var url = RutaConsulta(id);
        Response.Headers.RetryAfter = SegundosConsulta.ToString(CultureInfo.InvariantCulture);
        return Accepted(url, new clsCubicacionEnProcesoWeb(id, clsEstadosCubicacionRapida.EnProceso, url));
    }

    /// <summary>
    /// Opción D: estado de una cubicación que siguió en segundo plano. 200 <c>EstadoCubicacion</c> con <c>en_proceso</c> (y
    /// <c>Retry-After</c>), <c>completada</c> (con <c>resultado</c>) o <c>fallida</c> (con <c>error</c>); 404 si no existe, venció (se
    /// guardan <c>Cubicador:RetencionCubicacionMinutos</c>, o se perdieron al reiniciar Cotifly) o es de otro usuario.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType<clsEstadoCubicacionWeb>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status404NotFound)]
    public IActionResult Consultar(string id)
    {
        // Un id que no es un GUID responde el mismo 404 ErrorWeb que una cubicación que no existe.
        if (!Guid.TryParse(id, out var guid) || rapida.Obtener(UsuarioId, guid) is not { } cubicacion)
        {
            return NotFound(new clsErrorWeb(MensajeNoEncontrada));
        }

        if (!cubicacion.Terminada)
        {
            Response.Headers.RetryAfter = SegundosConsulta.ToString(CultureInfo.InvariantCulture);
        }

        return Ok(clsEstadoCubicacionWeb.Desde(cubicacion));
    }

    /// <summary>Ruta de consulta de la cubicación <paramref name="id"/> (<c>Location</c> y <c>consultar_en</c>).</summary>
    public static string RutaConsulta(Guid id) => "/web/cubicacion/" + id.ToString("D");
}
