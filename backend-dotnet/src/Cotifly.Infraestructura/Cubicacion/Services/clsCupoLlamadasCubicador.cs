using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Opción C (D3, E2E 2026-10-16): cupo global de este proceso para <c>POST /recomendaciones</c>. El motor del cubicador corre en un solo
/// proceso y bloquea su API mientras calcula (26 a 31 s con 1.100 unidades): una segunda llamada en paralelo no se calcula antes, solo
/// espera en su fila y gasta su propio límite. Un <see cref="SemaphoreSlim"/> singleton con <c>Cubicador:MaximoConcurrente</c> cupos (1 por
/// defecto) lo comparten todos los que piden una recomendación: "Calcular cubicaje" (también en segundo plano), la captura, el recálculo
/// y la licitación, porque todos pasan por <see cref="clsCubicadorHttp.RecomendarAsync"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>El POST y su reconsulta (<c>GET /recomendaciones</c> tras un POST sin respuesta) ocupan el MISMO cupo: si se soltara entre los
/// dos, otro POST pondría al motor a calcular otra carga y la reconsulta esperaría detrás.</item>
/// <item>Los catálogos y la sonda de salud van fuera del cupo: son GET livianos, con caché y límite corto propio; meterlos en el cupo
/// los haría esperar en Cotifly además de en el cubicador, sin aliviar al motor.</item>
/// <item>Con varias réplicas el cupo es por proceso; la licitación además usa el cupo en la base (<c>IClsCupoCubicador</c>, P6).</item>
/// </list>
/// </remarks>
public sealed class clsCupoLlamadasCubicador : IDisposable
{
    private readonly SemaphoreSlim _semaforo;

    public clsCupoLlamadasCubicador(IOptions<clsOpcionesCubicador> opciones)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        Maximo = Math.Max(1, opciones.Value.MaximoConcurrente);
        _semaforo = new SemaphoreSlim(Maximo, Maximo);
    }

    /// <summary>Cupos totales (<c>Cubicador:MaximoConcurrente</c>).</summary>
    public int Maximo { get; }

    /// <summary>Cupos libres en este momento (para pruebas y diagnóstico).</summary>
    public int Libres => _semaforo.CurrentCount;

    /// <summary>
    /// Toma un cupo esperando hasta <paramref name="espera"/>; <c>null</c> si no se liberó ninguno a tiempo. La cancelación de quien llama
    /// se propaga (<see cref="OperationCanceledException"/>). El cupo se suelta con <see cref="IDisposable.Dispose"/> (una sola vez).
    /// </summary>
    public async Task<IDisposable?> TomarAsync(TimeSpan espera, CancellationToken cancellationToken = default)
    {
        if (espera < TimeSpan.Zero)
        {
            espera = TimeSpan.Zero;
        }

        return await _semaforo.WaitAsync(espera, cancellationToken).ConfigureAwait(false) ? new clsLiberacion(_semaforo) : null;
    }

    public void Dispose() => _semaforo.Dispose();

    private sealed class clsLiberacion(SemaphoreSlim semaforo) : IDisposable
    {
        private int _liberado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberado, 1) != 0)
            {
                return;
            }

            try
            {
                semaforo.Release();
            }
            catch (ObjectDisposedException)
            {
                // El proceso se está apagando (el contenedor de DI ya soltó el cupo): no hay a quién devolverlo.
            }
        }
    }
}
