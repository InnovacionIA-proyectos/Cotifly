namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Fecha de negocio en la zona horaria de Botero Soto (<c>Zona:ZonaHoraria</c>, por defecto <c>America/Bogota</c>).
/// El contenedor corre en UTC: entre las 19:00 y las 24:00 de Bogotá la fecha UTC ya es la del día siguiente, así que
/// la fecha visible (fecha larga del PDF, folio <c>PCN-DDMMAAAA</c> de #12) nunca sale de <see cref="IClsReloj.AhoraUtc"/> directamente.
/// </summary>
public interface IClsCalendarioNegocio
{
    /// <summary>Fecha actual en la zona de negocio.</summary>
    DateOnly Hoy { get; }
}
