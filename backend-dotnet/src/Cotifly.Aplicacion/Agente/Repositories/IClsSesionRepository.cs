using Cotifly.Dominio.Sesiones;

namespace Cotifly.Aplicacion.Agente.Repositories;

/// <summary>
/// Puerto de sesiones de conversación y su historial (<c>Sesiones</c>, <c>EventosConversacion</c>).
/// Los cambios se escriben al guardar la unidad de trabajo.
/// </summary>
public interface IClsSesionRepository
{
    Task<clsSesion?> ObtenerAsync(string spaceId, string threadId, CancellationToken cancellationToken = default);

    /// <summary>Sesión existente o una nueva <c>activa</c> (con el reloj de la aplicación como última actividad).</summary>
    Task<clsSesion> ObtenerOCrearAsync(string spaceId, string threadId, Guid? usuarioId = null, CancellationToken cancellationToken = default);

    /// <summary>Registra actividad en la sesión (la crea si no existe). Sin <paramref name="cuando"/> usa el reloj de la aplicación.</summary>
    Task<clsSesion> TocarAsync(string spaceId, string threadId, DateTimeOffset? cuando = null, CancellationToken cancellationToken = default);

    /// <summary>Archiva la sesión si existe. Devuelve <c>false</c> si no existe.</summary>
    Task<bool> ArchivarAsync(string spaceId, string threadId, CancellationToken cancellationToken = default);

    /// <summary>Sesiones aún activas cuya última actividad es anterior a <paramref name="umbral"/> (barrido por TTL).</summary>
    Task<IReadOnlyList<clsSesion>> ListarInactivasAsync(DateTimeOffset umbral, CancellationToken cancellationToken = default);

    /// <summary>Sesiones de un space, de la más reciente a la más antigua (soporte).</summary>
    Task<IReadOnlyList<clsSesion>> ListarPorSpaceAsync(string spaceId, CancellationToken cancellationToken = default);

    /// <summary>Agrega un turno al historial.</summary>
    void AgregarEvento(clsEventoConversacion evento);

    /// <summary>Turnos del hilo en orden cronológico (el primero es la solicitud original).</summary>
    Task<IReadOnlyList<clsEventoConversacion>> HistorialAsync(string spaceId, string threadId, CancellationToken cancellationToken = default);
}
