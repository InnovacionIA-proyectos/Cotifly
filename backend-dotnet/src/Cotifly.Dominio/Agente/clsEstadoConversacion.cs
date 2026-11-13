namespace Cotifly.Dominio.Agente;

/// <summary>
/// Estado de la conversación del asistente por folio (ADR-018 §2b): la cotización sobre la que se conversa.
/// Uno por sesión; se persiste como JSON en <c>Sesiones.estadoAgente</c> (script 0002). Es inmutable y lo escribe solo el servidor
/// (el LLM nunca lo modifica). No lleva costo, precio, margen ni TRM: el asistente no ve montos.
/// </summary>
/// <remarks>
/// Las sesiones del agente cotizador legado (ADR-004) guardan en la base un JSON con más campos (cliente, ruta, costo, margen,
/// precio, TRM, candidatos...): al leerlas se ignoran los campos desconocidos (<c>System.Text.Json</c> ignora propiedades
/// desconocidas) y se conservan folio, cotización, versión y moneda.
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

    /// <summary>Moneda de la cotización (<c>COP</c> o <c>USD</c>).</summary>
    public string? Moneda { get; init; }
}
