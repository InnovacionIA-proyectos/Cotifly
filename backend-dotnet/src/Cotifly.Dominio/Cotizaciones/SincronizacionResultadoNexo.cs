namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Estado de la sincronización del resultado ganada/perdida con Nexo (ADR-023)
/// (<c>CotizacionesSolicitud.sincronizacionResultadoNexo</c>, script 13). Códigos snake_case (<see cref="Comun.clsCodigoEnum"/>).
/// <c>null</c> en la columna = sin resultado, o resultado anterior al 13 (ver <see cref="clsCotizacionSolicitud.EstadoSincronizacionResultado"/>).
/// </summary>
public enum SincronizacionResultadoNexo
{
    /// <summary>
    /// El comercial marcó el resultado cuando la cotización todavía no estaba Entregada en Nexo: quedó solo en Cotifly y el polling lo
    /// envía a Nexo una sola vez cuando la Mesa de Costeo la entregue.
    /// </summary>
    Pendiente,

    /// <summary>Nexo tiene el resultado (enviado en el momento o desde la cola de pendientes).</summary>
    Sincronizado,

    /// <summary>
    /// El resultado quedó solo en Cotifly y ya no se puede enviar a Nexo: la solicitud se descartó o venció en Nexo, Nexo ya no la
    /// reconoce o se agotaron los intentos. El motivo queda en <c>errorSincronizacionResultado</c>.
    /// </summary>
    NoSincronizable,
}
