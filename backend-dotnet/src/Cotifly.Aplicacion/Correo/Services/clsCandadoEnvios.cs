namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Serializa los envíos de una misma cotización dentro del proceso: sin esto, dos envíos simultáneos pasarían ambos la
/// deduplicación (ADR-008). Usa un número fijo de semáforos repartidos por el id de la
/// cotización (memoria acotada; dos cotizaciones pueden compartir semáforo y esperarse, sin otra consecuencia).
/// Solo cubre un proceso: con varias réplicas haría falta un candado en la base (<c>sp_getapplock</c>).
/// </summary>
public sealed class clsCandadoEnvios : IDisposable
{
    private const int Particiones = 64;
    private readonly SemaphoreSlim[] _semaforos = [.. Enumerable.Range(0, Particiones).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>Espera el turno de la cotización; liberar con el <see cref="IDisposable"/> devuelto.</summary>
    public async Task<IDisposable> TomarAsync(Guid cotizacionId, CancellationToken cancellationToken = default)
    {
        var semaforo = _semaforos[(cotizacionId.GetHashCode() & int.MaxValue) % Particiones];
        await semaforo.WaitAsync(cancellationToken);
        return new clsLiberador(semaforo);
    }

    public void Dispose()
    {
        foreach (var semaforo in _semaforos)
        {
            semaforo.Dispose();
        }
    }

    private sealed class clsLiberador(SemaphoreSlim semaforo) : IDisposable
    {
        private int _liberado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
            {
                semaforo.Release();
            }
        }
    }
}
