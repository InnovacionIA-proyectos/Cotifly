using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Límite de peticiones por minuto al proveedor de IA (<c>IA:Rpm</c>; evita el 429 de las capas gratuitas). Ventana deslizante de
/// 60 s: pasan hasta <c>Rpm</c> llamadas seguidas y la siguiente espera a que la más vieja salga de la ventana. El reloj y la espera
/// se inyectan para probarlo sin dormir.
/// </summary>
/// <remarks>
/// HU-013.16 (#126): el port de <c>LimitadorRPM</c> del Python espaciaba TODAS las llamadas 60/rpm segundos (7,5 s con el valor por
/// defecto 8), aunque el minuto tuviera cupo. Un turno del asistente hace dos o tres llamadas seguidas, así que esperaba 15 s solo
/// por el limitador: era la mayor parte de los 17 a 24 s por turno medidos en E2E. La ventana deslizante respeta el mismo tope por
/// minuto sin espaciar las llamadas de un turno.
/// </remarks>
public sealed class clsLimitadorRpm : IDisposable
{
    /// <summary>Ventana del límite.</summary>
    public static readonly TimeSpan Ventana = TimeSpan.FromMinutes(1);

    private readonly int _rpm;
    private readonly TimeProvider _tiempo;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;
    private readonly SemaphoreSlim _candado = new(1, 1);
    private readonly Queue<DateTimeOffset> _llamadas = new();

    public clsLimitadorRpm(int rpm, TimeProvider tiempo, Func<TimeSpan, CancellationToken, Task>? esperar = null)
    {
        ArgumentNullException.ThrowIfNull(tiempo);
        ArgumentOutOfRangeException.ThrowIfNegative(rpm);
        _rpm = rpm;
        _tiempo = tiempo;
        _esperar = esperar ?? ((espera, ct) => Task.Delay(espera, tiempo, ct));
    }

    /// <summary>Llamadas por minuto (0 = sin límite).</summary>
    public int Rpm => _rpm;

    /// <summary>Espera, si hace falta, a que haya cupo en la ventana y registra la llamada. Devuelve cuánto esperó.</summary>
    public async Task<TimeSpan> EsperarAsync(CancellationToken cancellationToken = default)
    {
        if (_rpm <= 0)
        {
            return TimeSpan.Zero;
        }

        await _candado.WaitAsync(cancellationToken);
        try
        {
            var ahora = _tiempo.GetUtcNow();
            Purgar(ahora);
            var esperado = TimeSpan.Zero;
            if (_llamadas.Count >= _rpm)
            {
                var espera = _llamadas.Peek() + Ventana - ahora;
                if (espera > TimeSpan.Zero)
                {
                    await _esperar(espera, cancellationToken);
                    esperado = espera;
                    ahora = _tiempo.GetUtcNow();
                }

                Purgar(ahora);
                while (_llamadas.Count >= _rpm)
                {
                    _llamadas.Dequeue(); // un reloj que no avanzó lo que se esperó no bloquea para siempre
                }
            }

            _llamadas.Enqueue(ahora);
            return esperado;
        }
        finally
        {
            _candado.Release();
        }
    }

    public void Dispose() => _candado.Dispose();

    private void Purgar(DateTimeOffset ahora)
    {
        while (_llamadas.Count > 0 && _llamadas.Peek() + Ventana <= ahora)
        {
            _llamadas.Dequeue();
        }
    }
}

/// <summary>
/// Decorador de <see cref="IChatClient"/> que respeta <see cref="clsLimitadorRpm"/> antes de cada llamada y registra la espera
/// (HU-013.16: es una de las fases del tiempo de un turno del asistente).
/// </summary>
public sealed class clsChatClientLimitado(IChatClient interno, clsLimitadorRpm limitador, ILogger<clsChatClientLimitado>? logger = null) : DelegatingChatClient(interno)
{
    private readonly ILogger _logger = logger ?? NullLogger<clsChatClientLimitado>.Instance;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var espera = await limitador.EsperarAsync(cancellationToken);
        if (espera > TimeSpan.Zero)
        {
            _logger.LogInformation("La llamada al proveedor de IA esperó {EsperaMs} ms por IA:Rpm={Rpm}.", (long)espera.TotalMilliseconds, limitador.Rpm);
        }

        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            limitador.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// <see cref="IChatClient"/> cuando <c>IA:Proveedor</c> está vacío (valor por defecto): la API arranca sin proveedor y cada turno
/// falla con un mensaje claro, que el canal convierte en el error controlado.
/// </summary>
public sealed class clsChatClientNoConfigurado : IChatClient
{
    public const string Mensaje = "El agente de IA no está configurado: define IA:Proveedor, IA:Modelo e IA:ApiKey.";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Mensaje);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Mensaje);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
