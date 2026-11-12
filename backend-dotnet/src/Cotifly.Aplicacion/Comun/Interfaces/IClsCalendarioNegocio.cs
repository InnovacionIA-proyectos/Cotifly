namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Fecha de negocio en la zona horaria de Botero Soto (<c>Zona:ZonaHoraria</c>, por defecto <c>America/Bogota</c>).
/// El contenedor corre en UTC: entre las 19:00 y las 24:00 de Bogotá la fecha UTC ya es la del día siguiente, así que
/// la fecha visible (fecha larga del PDF, folio <c>PCN-[nit-]INICIALES-DDMMAAAA-NNN</c>) nunca sale de <see cref="IClsReloj.AhoraUtc"/> directamente.
/// </summary>
public interface IClsCalendarioNegocio
{
    /// <summary>Fecha actual en la zona de negocio.</summary>
    DateOnly Hoy { get; }

    /// <summary>Id de la zona de negocio (p. ej. <c>America/Bogota</c>).</summary>
    string ZonaHoraria { get; }

    /// <summary>Fecha de negocio de un instante (mes de la serie de métricas).</summary>
    DateOnly FechaDe(DateTimeOffset instante);

    /// <summary>Instante en que empieza <paramref name="fecha"/> (00:00) en la zona de negocio (límites del periodo).</summary>
    DateTimeOffset InicioDelDia(DateOnly fecha);
}
