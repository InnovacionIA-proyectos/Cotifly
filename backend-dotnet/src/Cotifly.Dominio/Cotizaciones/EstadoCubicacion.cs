namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-023.5 (#160): estado de la cubicación en segundo plano de una ruta de licitación (<c>CotizacionLineasSolicitud.estadoCubicacion</c>,
/// propuesta <c>db/scripts/pendientes/04</c>). Códigos snake_case (<see cref="Comun.clsCodigoEnum"/>). <c>null</c> en la columna = la ruta se cubicó al
/// guardarla (formulario, licitación sin cubicador o anterior a HU-023.5): no hay nada pendiente.
/// </summary>
public enum EstadoCubicacion
{
    /// <summary>La ruta espera su turno (o un reintento) para pedirle los camiones al cubicador. Tiene un camión provisional sin vehículo.</summary>
    Pendiente,

    /// <summary>El cubicador respondió (o la ruta quedó con el respaldo): sus camiones son los definitivos.</summary>
    Cubicada,

    /// <summary>
    /// El cubicador rechazó la ruta o no respondió tras todos los reintentos. La licitación no viaja a Nexo hasta que el comercial
    /// reintente (o acepte el camión sin vehículo, <c>usar_respaldo</c>).
    /// </summary>
    Fallida,
}
