namespace Cotifly.Dominio.Agente;

/// <summary>
/// Estado de la conversación del asistente por folio (HU-013.6 y HU-013.9, ADR-018 §2b): la cotización sobre la que se conversa.
/// Uno por sesión; se persiste como JSON en <c>Sesiones.estadoAgente</c> (script 0002). Es inmutable y lo escribe solo el servidor
/// (el LLM nunca lo modifica). No lleva costo, precio, margen ni TRM: el asistente no ve montos.
/// </summary>
/// <remarks>
/// Hasta HU-013.9 (#105) este tipo era el estado del slot-filling del agente cotizador legado (ADR-004: cliente, ruta, costo, margen,
/// precio, TRM, candidatos...). Las sesiones antiguas de Google Chat y del chat sin folio conservan ese JSON en la base como
/// auditoría: al leerlas se ignoran los campos que ya no existen (<c>System.Text.Json</c> ignora propiedades desconocidas) y se
/// conservan los que sí (folio, cotización, versión y moneda).
/// </remarks>
public sealed record clsEstadoConversacion
{
    /// <summary>Estado inicial de una sesión nueva.</summary>
    public static clsEstadoConversacion Vacio { get; } = new();

    /// <summary>Folio de la cotización de la conversación.</summary>
    public string? FolioEnProgreso { get; init; }

    /// <summary>Id de la cotización de la conversación.</summary>
    public Guid? CotizacionId { get; init; }

    /// <summary>Versión de la cotización cuando empezó el turno.</summary>
    public int? Version { get; init; }

    /// <summary>Moneda de la cotización (<c>COP</c> o <c>USD</c>, HU-012.2).</summary>
    public string? Moneda { get; init; }
}
