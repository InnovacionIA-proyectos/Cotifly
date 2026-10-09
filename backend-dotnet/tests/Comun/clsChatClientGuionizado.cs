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
    /// <summary>Proveedor y modelo que informa el guion (<see cref="ChatClientMetadata"/>, como los proveedores reales).</summary>
    public const string Proveedor = "guion";

    public const string Modelo = "modelo-guionizado";

    private readonly ConcurrentQueue<Func<ChatResponse>> _guion = new();
    private readonly ConcurrentQueue<TimeSpan> _demoras = new();
    private Func<ChatResponse>? _repetir;
    private Exception? _rechazoEsquema;
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

    /// <summary>
    /// Cierra el turno: la respuesta sin herramientas que termina el loop y la de la llamada final (ADR-003) como respuesta
    /// estructurada válida (HU-003.10) de tipo <c>conversacional</c> con <paramref name="texto"/>.
    /// </summary>
    public clsChatClientGuionizado Cerrar(string texto) => Responder(texto).ResponderFinal(texto);

    /// <summary>
    /// Cierra un turno que armó una propuesta: con <c>IA:PropuestaDirecta</c> (HU-013.16) el runner pasa directo a la llamada final, que
    /// recibe esta respuesta estructurada de tipo <c>confirmacion_requerida</c>.
    /// </summary>
    public clsChatClientGuionizado Proponer(string texto) => ResponderFinal(texto, "confirmacion_requerida", true);

    /// <summary>
    /// Cierra el turno en la misma llamada del loop (HU-013.16, <c>IA:RespuestaFinalEnLoop</c>): la respuesta sin herramientas ya es
    /// el JSON válido de la respuesta final, así que no hay llamada final aparte.
    /// </summary>
    public clsChatClientGuionizado CerrarEnLoop(string texto, string tipo = "conversacional", bool requiereConfirmacion = false) =>
        ResponderFinal(texto, tipo, requiereConfirmacion);

    /// <summary>Respuesta de la llamada final con el JSON de <see cref="JsonFinal"/>.</summary>
    public clsChatClientGuionizado ResponderFinal(string texto, string tipo = "conversacional", bool requiereConfirmacion = false, params string[] acciones) =>
        Responder(JsonFinal(texto, tipo, requiereConfirmacion, acciones));

    /// <summary>JSON de la respuesta final estructurada (<c>clsRespuestaAgenteEstructurada</c>): <c>acciones_sugeridas</c> null si no hay.</summary>
    public static string JsonFinal(string texto, string tipo = "conversacional", bool requiereConfirmacion = false, params string[] acciones) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tipo"] = tipo,
            ["texto"] = texto,
            ["requiere_confirmacion"] = requiereConfirmacion,
            ["acciones_sugeridas"] = acciones is { Length: > 0 } ? acciones : null,
        });

    /// <summary>
    /// Simula un proveedor sin JSON Schema: toda llamada que pida <c>response_format</c> con esquema lanza <paramref name="error"/>
    /// (sin consumir el guion); las demás siguen el guion.
    /// </summary>
    public clsChatClientGuionizado RechazarJsonSchema(Exception error)
    {
        _rechazoEsquema = error;
        return this;
    }

    /// <summary>La siguiente llamada lanza <paramref name="error"/> (proveedor caído, 429...).</summary>
    public clsChatClientGuionizado Fallar(Exception error)
    {
        _guion.Enqueue(() => throw error);
        return this;
    }

    /// <summary>
    /// La siguiente llamada tarda <paramref name="demora"/> antes de seguir el guion (o hasta que la cancelen: entonces lanza
    /// <see cref="OperationCanceledException"/>, como un proveedor real). Para probar el tiempo máximo del redactor (HU-013.5).
    /// </summary>
    public clsChatClientGuionizado Demorar(TimeSpan demora)
    {
        _demoras.Enqueue(demora);
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
        if (_demoras.TryDequeue(out var demora))
        {
            return DemorarAsync(demora, options, cancellationToken);
        }

        return Siguiente(options);
    }

    private async Task<ChatResponse> DemorarAsync(TimeSpan demora, ChatOptions? options, CancellationToken cancellationToken)
    {
        await Task.Delay(demora, cancellationToken);
        return await Siguiente(options);
    }

    private Task<ChatResponse> Siguiente(ChatOptions? options)
    {
        if (_rechazoEsquema is not null && options?.ResponseFormat is ChatResponseFormatJson { Schema: not null })
        {
            return Task.FromException<ChatResponse>(_rechazoEsquema);
        }

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

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(Proveedor, null, Modelo) : null;

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
