using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Respuesta final del agente con esquema (HU-003.10, issue #16, ADR-003 "Salida estructurada"). Es lo único que el LLM entrega
/// al cerrar el turno; se pide con <c>ChatResponseFormat.ForJsonSchema</c> (esquema generado de este tipo) y se valida a mano en
/// <c>clsValidadorRespuestaAgente</c>. Es interna: hacia <c>/web/chat</c> solo sale <see cref="Texto"/>; precio, margen y PDF
/// salen de las herramientas del turno, nunca de aquí.
/// </summary>
[Description("Respuesta final del asistente de cotización para el comercial.")]
public sealed record clsRespuestaAgenteEstructurada
{
    /// <summary>Largo máximo de <see cref="Texto"/> (caracteres, sin espacios al borde).</summary>
    public const int LargoMaximoTexto = 4000;

    /// <summary>Máximo de acciones sugeridas.</summary>
    public const int MaximoAcciones = 2;

    /// <summary>Tipo de interacción.</summary>
    [JsonPropertyName("tipo")]
    [Description("conversacional: pregunta o respuesta simple. previsualizacion: presenta costo, margen y precio que calcularon las herramientas y pide confirmación. confirmacion_requerida: pide la confirmación explícita de una acción con efecto (PDF o correo). error: explica un error de negocio que devolvió una herramienta.")]
    public required TipoRespuestaAgente Tipo { get; init; }

    /// <summary>Texto para el comercial (1 a 4000 caracteres).</summary>
    [JsonPropertyName("texto")]
    [Description("Texto en español para el comercial, de 1 a 4000 caracteres. Solo montos y folios que aparecen en las salidas de las herramientas o en el estado resuelto.")]
    public required string Texto { get; init; }

    /// <summary>True solo con <c>previsualizacion</c> o <c>confirmacion_requerida</c>.</summary>
    [JsonPropertyName("requiere_confirmacion")]
    [Description("true solo si tipo es previsualizacion o confirmacion_requerida.")]
    public required bool RequiereConfirmacion { get; init; }

    /// <summary>Acciones que el comercial puede tomar (solo con una previsualización pendiente); <c>null</c> si no hay.</summary>
    [JsonPropertyName("acciones_sugeridas")]
    [Description("Solo con una previsualización pendiente de confirmar: confirmar_pdf y/o ajustar_margen. null en otro caso.")]
    public IReadOnlyList<AccionSugeridaAgente>? AccionesSugeridas { get; init; }

    /// <summary>Respuesta de respaldo cuando el modelo no produjo una respuesta válida tras el reintento.</summary>
    public static clsRespuestaAgenteEstructurada Respaldo(string texto) =>
        new() { Tipo = TipoRespuestaAgente.Error, Texto = texto, RequiereConfirmacion = false };
}

/// <summary>Tipo de interacción de la respuesta final (en el JSON, snake_case: <c>confirmacion_requerida</c>).</summary>
public enum TipoRespuestaAgente
{
    /// <summary>Pregunta o respuesta simple (slot filling, dudas).</summary>
    Conversacional,

    /// <summary>Resumen de costo, margen y precio calculados por las herramientas; pide confirmación.</summary>
    Previsualizacion,

    /// <summary>Pide la confirmación explícita de una acción con efecto externo (PDF, correo).</summary>
    ConfirmacionRequerida,

    /// <summary>Explica un error de negocio de una herramienta (o el respaldo del validador).</summary>
    Error,
}

/// <summary>
/// Acción sugerida al comercial. Cada una corresponde a una herramienta del plugin <c>cotizador</c> que debe estar declarada en el
/// turno (<see cref="clsNombresHerramientas"/>); en Google Chat se muestran como botones.
/// </summary>
public enum AccionSugeridaAgente
{
    /// <summary>Confirmar la previsualización y generar el PDF (<c>generar_pdf</c>).</summary>
    ConfirmarPdf,

    /// <summary>Ajustar el margen antes de generar el PDF (<c>registrar_ajuste_margen</c>).</summary>
    AjustarMargen,
}
