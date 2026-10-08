using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Dominio.Agente;
using Microsoft.Extensions.AI;

namespace Cotifly.Aplicacion.Agente.Interfaces;

/// <summary>
/// Respuesta final validada del agente (HU-003.10, issue #16, ADR-003 "Salida estructurada"). Terminado el loop de herramientas,
/// el runner delega aquí la llamada final: una llamada aparte, sin herramientas, que pide
/// <see cref="clsRespuestaAgenteEstructurada"/> con <c>ChatResponseFormat.ForJsonSchema</c>, la valida a mano y, si no pasa,
/// reintenta una vez con un mensaje de corrección. Si vuelve a fallar devuelve el respaldo controlado
/// (<see cref="clsRespuestaFinalAgente.EsRespaldo"/>): nunca sale una respuesta sin validar.
/// </summary>
public interface IClsValidadorRespuestaAgente
{
    /// <summary>
    /// Pide y valida la respuesta final. Un fallo del proveedor (429, 5xx, red) y la cancelación se propagan como excepción; un
    /// JSON inválido nunca lanza.
    /// </summary>
    /// <param name="chat">Cliente del modelo del turno (el mismo del loop).</param>
    /// <param name="mensajes">Conversación del turno (sistema, historial, llamadas y resultados de herramientas). No se modifica.</param>
    /// <param name="contexto">Lo que respalda montos, folios y acciones: estado, salidas de herramientas, mensajes del comercial.</param>
    Task<clsRespuestaFinalAgente> ObtenerRespuestaFinalAsync(
        IChatClient chat, IReadOnlyList<ChatMessage> mensajes, clsContextoValidacionRespuesta contexto, CancellationToken cancellationToken = default);
}

/// <summary>Datos del turno contra los que se valida la respuesta final (nada de esto sale del LLM).</summary>
/// <param name="Estado">Estado de la conversación (el folio de la cotización: respalda los folios del texto; no tiene montos).</param>
/// <param name="Salidas">Resultados de las herramientas del turno, en orden.</param>
/// <param name="MensajesDelComercial">Mensajes del comercial en la conversación (un monto que él escribió puede repetirse).</param>
/// <param name="HerramientasDeclaradas">Herramientas declaradas al modelo en el turno (una acción sugerida debe corresponder a una).</param>
public sealed record clsContextoValidacionRespuesta(
    clsEstadoConversacion Estado,
    IReadOnlyList<clsResultadoHerramienta> Salidas,
    IReadOnlyList<string> MensajesDelComercial,
    IReadOnlyCollection<string> HerramientasDeclaradas);

/// <summary>Resultado de <see cref="IClsValidadorRespuestaAgente.ObtenerRespuestaFinalAsync"/>.</summary>
/// <param name="Respuesta">Respuesta validada, o el respaldo <c>{tipo: error}</c>.</param>
/// <param name="Llamadas">Llamadas al modelo que hizo (1, o 2 con el reintento).</param>
/// <param name="Reintentos">Reintentos por validación fallida (0 o 1).</param>
/// <param name="EsRespaldo">True si ningún intento pasó la validación y <see cref="Respuesta"/> es el error controlado.</param>
/// <param name="Motivos">Códigos de los fallos de validación de todos los intentos (para métricas y logs; sin contenido del texto).</param>
public sealed record clsRespuestaFinalAgente(
    clsRespuestaAgenteEstructurada Respuesta,
    int Llamadas,
    int Reintentos,
    bool EsRespaldo,
    IReadOnlyList<string> Motivos);
