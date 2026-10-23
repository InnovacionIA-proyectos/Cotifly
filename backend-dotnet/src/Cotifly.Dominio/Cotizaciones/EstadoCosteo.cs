namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Estado del costeo en Nexo (<c>Cotizaciones.estadoCosteo</c>, HU-005.5 a HU-005.7, ADR-015). Códigos snake_case
/// (<see cref="Comun.clsCodigoEnum"/>). <c>null</c> en la columna = la cotización no pasa por Nexo (costo manual del chat).
/// </summary>
/// <remarks>
/// Los tres valores derivados de Nexo (<see cref="PendienteCosteo"/>, <see cref="Autocosteo"/> y <see cref="Entregada"/>) siguen la
/// regla de la HU-005.7 (cruce de líneas de la solicitud contra líneas de la cotización de Nexo). <see cref="Descartada"/> cubre el
/// cuarto estado real de la solicitud en Nexo, que el diseño original no tenía (ver <c>docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md</c>).
/// Los demás son propios de Cotifly: el envío (<see cref="PendienteEnvio"/>, <see cref="ErrorEnvio"/>) y el fin del polling sin
/// costeo (<see cref="CosteoVencido"/>).
/// </remarks>
public enum EstadoCosteo
{
    /// <summary>La cotización se guardó y todavía no se envió a Nexo (o el envío quedó a medias).</summary>
    PendienteEnvio,

    /// <summary>Nexo no recibió la solicitud (tiempo agotado, 4xx, 5xx, red): el comercial puede reintentar sin volver a llenar el formulario.</summary>
    ErrorEnvio,

    /// <summary>Nexo recibió la solicitud y al menos una línea sigue sin flete.</summary>
    PendienteCosteo,

    /// <summary>Todas las líneas tienen flete y la cotización de Nexo sigue en <c>Borrador</c>.</summary>
    Autocosteo,

    /// <summary>La cotización de Nexo está <c>Entregada</c>.</summary>
    Entregada,

    /// <summary>La solicitud de Nexo está <c>Descartada</c>: no habrá costeo.</summary>
    Descartada,

    /// <summary>El polling llegó a la edad máxima sin costeo: se deja de consultar y queda visible en "Mis cotizaciones".</summary>
    CosteoVencido,

    /// <summary>
    /// HU-028.1 (#183, Nexo #320): la Mesa de Costeo de Nexo rechazó la línea del CAMIÓN con un motivo (<c>estadoLinea = "rechazada"</c>):
    /// no se costea, no cuenta en el precio ni en los totales y no recibe conexos. Distinto de <see cref="Descartada"/> (la línea se
    /// borró, #134): la línea rechazada sigue existiendo en Nexo y la Mesa puede deshacer el rechazo mientras la cotización no esté
    /// entregada. Una ruta con todos sus camiones rechazados queda <c>rechazada</c>, sin precio. La cabecera nunca queda en este estado
    /// (<see cref="clsCotizacionSolicitud.EstadoCabecera"/>): si todas sus rutas están rechazadas resume lo que diga Nexo de la solicitud.
    /// </summary>
    Rechazada,
}
