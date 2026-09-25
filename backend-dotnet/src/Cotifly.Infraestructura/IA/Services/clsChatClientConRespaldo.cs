using System.Runtime.CompilerServices;
using Cotifly.Aplicacion.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Decorador de <see cref="IChatClient"/> con proveedores de respaldo (<c>IA:Fallbacks</c>, ADR-003; equivalente a los
/// <c>fallbacks</c> de LiteLLM del Python): si un proveedor responde 429, un 5xx, falla la red o agota su tiempo, la misma
/// petición pasa al siguiente. Un error del cliente (400, 401...) o la cancelación del llamador se propagan sin probar otro.
/// </summary>
public sealed class clsChatClientConRespaldo : IChatClient
{
    private readonly IReadOnlyList<IChatClient> _clientes;
    private readonly ILogger<clsChatClientConRespaldo> _logger;

    public clsChatClientConRespaldo(IReadOnlyList<IChatClient> clientes, ILogger<clsChatClientConRespaldo> logger)
    {
        ArgumentNullException.ThrowIfNull(clientes);
        ArgumentNullException.ThrowIfNull(logger);
        if (clientes.Count == 0)
        {
            throw new ArgumentException("Se necesita al menos un proveedor.", nameof(clientes));
        }

        _clientes = clientes;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var mensajes = messages as IList<ChatMessage> ?? messages.ToList();
        for (var i = 0; ; i++)
        {
            try
            {
                return await _clientes[i].GetResponseAsync(mensajes, options, cancellationToken);
            }
            catch (Exception ex) when (i < _clientes.Count - 1 && EsRecuperable(ex, cancellationToken))
            {
                _logger.LogWarning("Proveedor de IA {Indice} falló ({Tipo}, estado {Estado}); se prueba el respaldo {Siguiente}.", i, ex.GetType().Name, Estado(ex)?.ToString() ?? "sin estado", i + 1);
            }
        }
    }

    /// <summary>El agente no usa streaming: se atiende con el primer proveedor que abra la respuesta.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var actualizacion in _clientes[0].GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return actualizacion;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType is null ? throw new ArgumentNullException(nameof(serviceType)) : serviceType.IsInstanceOfType(this) ? this : _clientes[0].GetService(serviceType, serviceKey);

    public void Dispose()
    {
        foreach (var cliente in _clientes)
        {
            cliente.Dispose();
        }
    }

    /// <summary>¿Vale la pena probar otro proveedor? 429, 5xx, red caída o tiempo agotado (no la cancelación del llamador).</summary>
    public static bool EsRecuperable(Exception error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (Estado(error) is { } estado)
        {
            return estado == 429 || estado >= 500;
        }

        return error is HttpRequestException or TimeoutException or TaskCanceledException
               || error.InnerException is HttpRequestException or TimeoutException or IOException;
    }

    /// <summary>
    /// Código HTTP de la excepción o de sus internas: <see cref="HttpRequestException.StatusCode"/> o una propiedad <c>Status</c> /
    /// <c>StatusCode</c> numérica (<c>ClientResultException</c> del SDK de OpenAI, excepciones del SDK de Anthropic).
    /// </summary>
    public static int? Estado(Exception? error) => clsEstadoHttpExcepcion.Estado(error);
}
