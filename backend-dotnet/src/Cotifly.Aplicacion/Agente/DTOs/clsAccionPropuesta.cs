namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// <c>AccionPropuesta</c> de <c>RespuestaChat</c> (ADR-018 §2b): la acción final que el asistente PROPONE. La arma el
/// SERVIDOR con datos validados: el LLM solo eligió la herramienta y sus argumentos (margen, motivo, cambios de carga o servicio y
/// cantidad). El frontend la muestra con <see cref="Resumen"/> y un botón Confirmar; solo al confirmar llama a <see cref="Metodo"/>
/// <see cref="Endpoint"/> con <see cref="Cuerpo"/> tal cual e <c>If-Match: </c><see cref="RowVersion"/>. La IA nunca la ejecuta.
/// </summary>
/// <param name="Tipo"><c>ajustar_margen</c> | <c>re_solicitar_costeo</c> | <c>agregar_servicios_conexos</c>.</param>
/// <param name="Metodo"><c>PUT</c> | <c>POST</c>.</param>
/// <param name="Endpoint">Ruta relativa con el folio de la conversación (nunca otro).</param>
/// <param name="Cuerpo">Cuerpo JSON exacto del endpoint destino (claves snake_case del contrato).</param>
/// <param name="Resumen">
/// Texto para el botón de confirmación, armado por código con plantilla y precios calculados por el servidor (hasta 300 caracteres). Nunca
/// pasa por el LLM ni queda en el historial de la conversación.
/// </param>
/// <param name="RowVersion">Versión de la cotización que vio el asistente al proponer (el <c>ETag</c>); va en <c>If-Match</c>.</param>
public sealed record clsAccionPropuesta(
    string Tipo,
    string Metodo,
    string Endpoint,
    IReadOnlyDictionary<string, object?> Cuerpo,
    string Resumen,
    string? RowVersion)
{
    public const string TipoAjustarMargen = "ajustar_margen";
    public const string TipoReSolicitarCosteo = "re_solicitar_costeo";
    public const string TipoAgregarServiciosConexos = "agregar_servicios_conexos";

    /// <summary>Largo máximo de <see cref="Resumen"/> (contrato).</summary>
    public const int LargoMaximoResumen = 300;
}
