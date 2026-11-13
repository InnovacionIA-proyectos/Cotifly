using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// Tubería estándar de resiliencia común a las fuentes de la TRM (ADR-014): <paramref name="total"/> para toda la consulta,
/// un tercio por intento y <see cref="Reintentos"/> reintentos ante 5xx, 408, 429, fallos de red o intento agotado (GET, idempotente).
/// Cada fuente le pasa su propio tiempo: <c>Trm:TiempoEsperaSegundos</c> (Superintendencia) y <c>Trm:TiempoEsperaDolarApiSegundos</c>
/// (DolarApi, más corto para no demorar el respaldo).
/// </summary>
public static class clsResilienciaTrm
{
    /// <summary>Reintentos ante fallos transitorios (además del primer intento).</summary>
    public const int Reintentos = 2;

    /// <summary>Intentos mínimos en la ventana del circuito para que pueda abrir.</summary>
    public const int UmbralCircuito = 5;

    private static readonly TimeSpan _esperaEntreIntentos = TimeSpan.FromMilliseconds(200);

    /// <summary>Configura <paramref name="resiliencia"/> con el tiempo total <paramref name="total"/>.</summary>
    public static void Configurar(HttpStandardResilienceOptions resiliencia, TimeSpan total)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        if (total <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(total), total, "El tiempo de espera de la TRM debe ser positivo.");
        }

        var porIntento = total / (Reintentos + 1);
        resiliencia.TotalRequestTimeout.Timeout = total;
        resiliencia.AttemptTimeout.Timeout = porIntento;
        resiliencia.Retry.MaxRetryAttempts = Reintentos;
        resiliencia.Retry.Delay = _esperaEntreIntentos;
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;

        // Circuito: con el valor por defecto (100 peticiones) nunca abriría con el volumen de la TRM (una consulta por día y réplica, más
        // los reintentos). Abre con 5 intentos en 60 s si la mitad falla y corta 30 s. La ventana debe ser al menos el doble del intento.
        var ventana = TimeSpan.FromSeconds(60);
        resiliencia.CircuitBreaker.MinimumThroughput = UmbralCircuito;
        resiliencia.CircuitBreaker.FailureRatio = 0.5;
        resiliencia.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        resiliencia.CircuitBreaker.SamplingDuration = ventana >= porIntento * 2 ? ventana : porIntento * 2;
    }
}
