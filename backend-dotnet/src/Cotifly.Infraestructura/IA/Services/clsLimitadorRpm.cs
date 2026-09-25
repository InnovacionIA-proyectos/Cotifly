using Microsoft.Extensions.AI;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Límite de peticiones por minuto al proveedor de IA (port de <c>LimitadorRPM</c> de <c>orquestador/agente.py</c>): si se supera
/// <c>IA:Rpm</c>, la siguiente llamada espera a que se libere una ranura (intervalo fijo de 60/rpm segundos). Evita el 429 de las
/// capas gratuitas. El reloj y la espera se inyectan para probarlo sin dormir.
/// </summary>
public sealed class clsLimitadorRpm : IDisposable
{
    private readonly TimeSpan _intervalo;
    private readonly TimeProvider _tiempo;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;
    private readonly SemaphoreSlim _candado = new(1, 1);
    private DateTimeOffset _proxima = DateTimeOffset.MinValue;

    public clsLimitadorRpm(int rpm, TimeProvider tiempo, Func<TimeSpan, CancellationToken, Task>? esperar = null)
    {
        ArgumentNullException.ThrowIfNull(tiempo);
        ArgumentOutOfRangeException.ThrowIfNegative(rpm);
        _intervalo = rpm > 0 ? TimeSpan.FromMinutes(1) / rpm : TimeSpan.Zero;
        _tiempo = tiempo;
        _esperar = esperar ?? ((espera, ct) => Task.Delay(espera, tiempo, ct));
    }

    public TimeSpan Intervalo => _intervalo;

    public async Task EsperarAsync(CancellationToken cancellationToken = default)
    {
        if (_intervalo <= TimeSpan.Zero)
        {
            return;
        }

        await _candado.WaitAsync(cancellationToken);
        try
        {
            var ahora = _tiempo.GetUtcNow();
            var espera = _proxima - ahora;
            if (espera > TimeSpan.Zero)
            {
                await _esperar(espera, cancellationToken);
                ahora = _tiempo.GetUtcNow();
            }

            _proxima = (ahora > _proxima ? ahora : _proxima) + _intervalo;
        }
        finally
        {
            _candado.Release();
        }
    }

    public void Dispose() => _candado.Dispose();
}

/// <summary>Decorador de <see cref="IChatClient"/> que respeta <see cref="clsLimitadorRpm"/> antes de cada llamada.</summary>
public sealed class clsChatClientLimitado(IChatClient interno, clsLimitadorRpm limitador) : DelegatingChatClient(interno)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await limitador.EsperarAsync(cancellationToken);
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
