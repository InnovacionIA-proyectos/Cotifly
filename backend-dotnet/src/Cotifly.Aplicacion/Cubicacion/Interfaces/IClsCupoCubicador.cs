namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// HU-023.5 (#160), petición P6: cupos de llamadas al cubicador en paralelo, compartidos por TODAS las réplicas de Cotifly. Cada llamada
/// de la cubicación en segundo plano toma un cupo antes de llamar y lo suelta al terminar; si no hay cupo libre, la ruta espera a la
/// siguiente vuelta. La implementación de Infraestructura usa <c>sp_getapplock</c> (uno por cupo, de sesión): si la réplica muere, SQL
/// Server suelta su cupo solo.
/// </summary>
public interface IClsCupoCubicador
{
    /// <summary>
    /// Un cupo libre de los <paramref name="maximo"/> (sin esperar), o <c>null</c> si están todos tomados. Se suelta con
    /// <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Task<IAsyncDisposable?> IntentarTomarAsync(int maximo, CancellationToken cancellationToken = default);

    /// <summary>
    /// El candado del envío a Nexo de la licitación <paramref name="cotizacionId"/> tras cubicarla (sin esperar), o <c>null</c> si otra
    /// réplica (u otra tarea) lo tiene: mientras dura el envío nadie más revisa ni reclama esa licitación. Se suelta con
    /// <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Task<IAsyncDisposable?> IntentarTomarEnvioAsync(Guid cotizacionId, CancellationToken cancellationToken = default);
}
