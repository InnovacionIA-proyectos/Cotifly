namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Tipo de la solicitud de Nexo (<c>Cotizaciones.tipoSolicitud</c>, campo <c>tipo</c> de <c>CapturaSolicitudRequest</c>). Códigos:
/// <c>spot</c> | <c>licitacion</c>, los mismos que acepta Nexo en el cuerpo (en minúscula).
/// </summary>
public enum TipoSolicitud
{
    Spot,
    Licitacion,
}
