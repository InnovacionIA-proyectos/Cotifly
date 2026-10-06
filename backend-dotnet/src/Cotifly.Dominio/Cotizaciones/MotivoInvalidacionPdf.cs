namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Acción del comercial que invalidó el PDF de una cotización no enviada (ADR-018 §11; <c>CotizacionPdfsHistorial.motivo</c>).
/// Códigos: <c>ajuste_margen</c> | <c>servicios_conexos</c> | <c>textos</c> | <c>re_solicitud</c>.
/// </summary>
public enum MotivoInvalidacionPdf
{
    AjusteMargen,
    ServiciosConexos,
    Textos,
    ReSolicitud,
}
