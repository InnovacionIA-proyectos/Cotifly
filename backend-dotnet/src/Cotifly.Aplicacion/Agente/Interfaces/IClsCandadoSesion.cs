namespace Cotifly.Aplicacion.Agente.Interfaces;

/// <summary>
/// Candado de un turno por sesión: dos mensajes simultáneos de la misma conversación no corren a la vez
/// (se pisarían el estado y el historial). En SQL Server: <c>sp_getapplock</c> con dueño <c>Session</c> sobre una conexión propia,
/// recurso <c>cotifly:sesion:{spaceId}:{threadId}</c>; vale entre procesos y réplicas.
/// </summary>
public interface IClsCandadoSesion
{
    /// <summary>
    /// Toma el candado esperando como máximo <paramref name="espera"/>. Devuelve el candado tomado (se libera al desecharlo) o
    /// <c>null</c> si otro turno de la misma sesión sigue en curso.
    /// </summary>
    Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default);
}
