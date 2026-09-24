namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Estado de envío de la cotización (<c>Cotizaciones.estadoEnvio</c>).
/// Códigos: <c>sin_enviar</c> | <c>enviada</c> | <c>reenviada</c> (ver <see cref="Comun.clsCodigoEnum"/>).
/// </summary>
public enum EstadoEnvio
{
    SinEnviar,
    Enviada,
    Reenviada,
}
