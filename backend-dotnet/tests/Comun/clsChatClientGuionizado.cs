using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// <see cref="IChatClient"/> guionizado (port de <c>_LLMFake</c>/<c>_LLMGuion</c> de <c>tests/test_agente.py</c>): entrega las
/// respuestas en el orden cargado y guarda una copia de cada llamada (mensajes y opciones). Las llamadas a herramientas llevan los
/// argumentos como <see cref="JsonElement"/>, igual que los proveedores reales. Sin red ni tokens.
/// </summary>
public sealed class clsChatClientGuionizado : IChatClient
{
    private readonly ConcurrentQueue<Func<ChatResponse>> _guion = new();
    private Func<ChatResponse>? _repetir;
    private int _secuencia;

    /// <summary>Llamadas recibidas, en orden, con una copia de los mensajes de ese momento.</summary>
    public List<clsLlamadaModelo> Llamadas { get; } = [];

    /// <summary>El modelo pide una herramienta (con texto opcional antes de la llamada).</summary>
    public clsChatClientGuionizado Llamar(string herramienta, object argumentos, string? texto = null) =>
        LlamarVarias(texto, (herramienta, argumentos));

    /// <summary>El modelo pide varias herramientas en la misma respuesta.</summary>
    public clsChatClientGuionizado LlamarVarias(string? texto, params (string Herramienta, object Argumentos)[] llamadas)
    {
        ArgumentNullException.ThrowIfNull(llamadas);
        _guion.Enqueue(() => RespuestaConLlamadas(texto, llamadas));
        return this;
    }

    /// <summary>El modelo responde texto (fin del loop o llamada final).</summary>
    public clsChatClientGuionizado Responder(string texto)
    {
        _guion.Enqueue(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, texto)) { FinishReason = ChatFinishReason.Stop });
        return this;
    }

    /// <summary>Cierra el turno: la respuesta sin herramientas que termina el loop y la de la llamada final (ADR-003).</summary>
    public clsChatClientGuionizado Cerrar(string texto) => Responder(texto).Responder(texto);

    /// <summary>La siguiente llamada lanza <paramref name="error"/> (proveedor caído, 429...).</summary>
    public clsChatClientGuionizado Fallar(Exception error)
    {
        _guion.Enqueue(() => throw error);
        return this;
    }

    /// <summary>Cuando el guion se agota, repite siempre esta llamada a herramienta (para forzar el tope de iteraciones).</summary>
    public clsChatClientGuionizado RepetirSiempre(string herramienta, object argumentos)
    {
        _repetir = () => RespuestaConLlamadas(null, [(herramienta, argumentos)]);
        return this;
    }

    /// <summary>Respuestas del guion que no se consumieron.</summary>
    public int Pendientes => _guion.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Llamadas.Add(new clsLlamadaModelo([.. messages.Select(m => m.Clone())], options?.Clone()));
        if (_guion.TryDequeue(out var siguiente))
        {
            return Task.FromResult(siguiente());
        }

        return _repetir is not null
            ? Task.FromResult(_repetir())
            : throw new InvalidOperationException($"El guion del modelo se agotó en la llamada {Llamadas.Count}.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("El agente no usa streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>Argumentos como los entrega un proveedor: cada propiedad del objeto anónimo como <see cref="JsonElement"/>.</summary>
    public static Dictionary<string, object?> Argumentos(object argumentos)
    {
        var elemento = JsonSerializer.SerializeToElement(argumentos);
        return elemento.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    private ChatResponse RespuestaConLlamadas(string? texto, IEnumerable<(string Herramienta, object Argumentos)> llamadas)
    {
        var contenidos = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(texto))
        {
            contenidos.Add(new TextContent(texto));
        }

        foreach (var (herramienta, argumentos) in llamadas)
        {
            contenidos.Add(new FunctionCallContent($"llamada_{Interlocked.Increment(ref _secuencia)}", herramienta, Argumentos(argumentos)));
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contenidos)) { FinishReason = ChatFinishReason.ToolCalls };
    }
}

/// <summary>Una llamada al modelo: los mensajes (copia) y las opciones (herramientas, formato).</summary>
public sealed record clsLlamadaModelo(IReadOnlyList<ChatMessage> Mensajes, ChatOptions? Opciones)
{
    /// <summary>Nombres de las herramientas declaradas en la llamada.</summary>
    public IReadOnlyList<string> Herramientas => Opciones?.Tools?.Select(t => t.Name).ToList() ?? [];

    /// <summary>Texto del mensaje de sistema.</summary>
    public string Sistema => Mensajes.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;

    /// <summary>Resultados de herramientas que recibió el modelo en esta llamada (JSON), en orden.</summary>
    public IReadOnlyList<string> ResultadosHerramientas =>
        Mensajes.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? string.Empty).ToList();
}
