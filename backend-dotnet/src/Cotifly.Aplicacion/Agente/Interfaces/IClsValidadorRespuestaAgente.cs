using Cotifly.Dominio.Comun;
using Microsoft.Extensions.AI;

namespace Cotifly.Aplicacion.Agente.Interfaces;

/// <summary>
/// Validación de la respuesta final del agente (HU-003.10, issue #16). Terminado el loop de herramientas, el runner pide la
/// respuesta en una llamada aparte sin herramientas con <see cref="FormatoRespuesta"/> y la pasa por <see cref="ValidarAsync"/>
/// (ADR-003). En #12 solo existe el puerto con el validador de paso (<c>clsValidadorRespuestaPasante</c>): texto libre, sin
/// esquema. #16 registra el validador con <c>ChatResponseFormat.ForJsonSchema</c>, validación manual y un reintento.
/// </summary>
public interface IClsValidadorRespuestaAgente
{
    /// <summary>Formato que se pide al modelo en la llamada final (<c>null</c> = texto libre).</summary>
    ChatResponseFormat? FormatoRespuesta { get; }

    /// <summary>
    /// Valida la respuesta cruda del modelo y devuelve el texto para el comercial. Un fallo lo convierte el runner en el error
    /// controlado del turno (<c>200 {texto, error: true}</c> en <c>/web/chat</c>).
    /// </summary>
    Task<clsResultado<string>> ValidarAsync(string? respuesta, CancellationToken cancellationToken = default);
}
