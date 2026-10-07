namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Canales de chat (sección <c>Chat</c>, HU-013.6, #102, ADR-018 §1 y §2b).
/// </summary>
public sealed class clsOpcionesChat
{
    public const string Seccion = "Chat";

    /// <summary>
    /// ¿El chat todavía crea cotizaciones (el agente cotizador de HU-003 con <c>generar_pdf</c>, <c>calcular_precio</c>,
    /// <c>obtener_margen</c>, <c>buscar_tarifa</c>...)? Por defecto <c>false</c>: <c>POST /web/chat</c> exige <c>folio</c> y solo corre
    /// el asistente de solo lectura y propuesta (<see cref="clsAsistenteFolioRunner"/>), y Google Chat responde que la creación se
    /// retiró. Con <c>true</c> (solo para una transición controlada) un mensaje SIN <c>folio</c> vuelve al agente legado; con
    /// <c>folio</c> siempre responde el asistente. El código legado se borra en una issue posterior.
    /// </summary>
    public bool CreacionHabilitada { get; set; }
}
