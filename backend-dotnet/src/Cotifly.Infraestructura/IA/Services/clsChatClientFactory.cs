using System.ClientModel;
using Anthropic;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Construye el <see cref="IChatClient"/> del agente según <c>IA:Proveedor</c> (ADR-003, Factory): el principal, los respaldos
/// de <c>IA:Fallbacks</c> (<see cref="clsChatClientConRespaldo"/>), el limitador de <c>IA:Rpm</c> (<see cref="clsChatClientLimitado"/>)
/// y el registro de llamadas de Microsoft.Extensions.AI (<c>UseLogging</c>, que solo escribe el contenido en Trace). Es el único
/// punto que conoce los SDK de los proveedores. No hace ninguna llamada de red al construir.
/// </summary>
public static class clsChatClientFactory
{
    public const string EndpointGemini = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public const string EndpointOllama = "http://localhost:11434/v1/";

    /// <summary>Clave de relleno para endpoints compatibles sin autenticación (el SDK de OpenAI exige una).</summary>
    public const string ClaveVacia = "sin-clave";

    /// <summary>Cliente registrado en DI (singleton).</summary>
    public static IChatClient Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        var opciones = servicios.GetRequiredService<IOptions<clsOpcionesIA>>().Value;
        var registros = servicios.GetRequiredService<ILoggerFactory>();
        return Crear(opciones, registros, servicios.GetService<TimeProvider>() ?? TimeProvider.System);
    }

    public static IChatClient Crear(clsOpcionesIA opciones, ILoggerFactory registros, TimeProvider tiempo)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(registros);
        ArgumentNullException.ThrowIfNull(tiempo);

        if (string.IsNullOrWhiteSpace(opciones.Proveedor))
        {
            return new clsChatClientNoConfigurado();
        }

        var cadena = new List<IChatClient> { CrearProveedor(opciones.Principal(), opciones.TimeoutSeg) };
        cadena.AddRange(opciones.Fallbacks.Select(f => CrearProveedor(f, opciones.TimeoutSeg)));

        IChatClient cliente = cadena.Count == 1 ? cadena[0] : new clsChatClientConRespaldo(cadena, registros.CreateLogger<clsChatClientConRespaldo>());
        if (opciones.Rpm > 0)
        {
            cliente = new clsChatClientLimitado(cliente, new clsLimitadorRpm(opciones.Rpm, tiempo));
        }

        return new ChatClientBuilder(cliente).UseLogging(registros).Build();
    }

    /// <summary>Cliente de un proveedor (sin respaldo ni limitador).</summary>
    public static IChatClient CrearProveedor(clsOpcionesProveedorIA proveedor, int timeoutSeg = 60)
    {
        ArgumentNullException.ThrowIfNull(proveedor);
        var nombre = proveedor.Proveedor?.Trim().ToLowerInvariant();
        var modelo = proveedor.Modelo?.Trim() ?? throw new InvalidOperationException("IA:Modelo es obligatorio.");
        var timeout = TimeSpan.FromSeconds(timeoutSeg);

        return nombre switch
        {
            clsOpcionesIA.OpenAi => OpenAiCompatible(proveedor.ApiKey, proveedor.Endpoint, modelo, timeout),
            clsOpcionesIA.AzureOpenAi => OpenAiCompatible(proveedor.ApiKey, EndpointAzure(proveedor.Endpoint), modelo, timeout),
            clsOpcionesIA.OpenAiCompatible => OpenAiCompatible(proveedor.ApiKey, proveedor.Endpoint, modelo, timeout),
            clsOpcionesIA.Gemini => OpenAiCompatible(proveedor.ApiKey, Vacio(proveedor.Endpoint) ?? EndpointGemini, modelo, timeout),
            clsOpcionesIA.Ollama => OpenAiCompatible(proveedor.ApiKey, Vacio(proveedor.Endpoint) ?? EndpointOllama, modelo, timeout),
            clsOpcionesIA.Anthropic => AnthropicCliente(proveedor.ApiKey, proveedor.Endpoint, modelo),
            _ => throw new InvalidOperationException($"IA:Proveedor '{proveedor.Proveedor}' no es válido."),
        };
    }

    /// <summary>API v1 de Azure OpenAI con el SDK de OpenAI: <c>https://{recurso}.openai.azure.com/openai/v1/</c>.</summary>
    public static string EndpointAzure(string? endpoint)
    {
        var raiz = Vacio(endpoint) ?? throw new InvalidOperationException("IA:Endpoint es obligatorio con azure_openai.");
        raiz = raiz.TrimEnd('/');
        return raiz.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? raiz + "/" : raiz + "/openai/v1/";
    }

    private static IChatClient OpenAiCompatible(string? clave, string? endpoint, string modelo, TimeSpan timeout)
    {
        var opciones = new OpenAIClientOptions { NetworkTimeout = timeout };
        if (Vacio(endpoint) is { } url)
        {
            opciones.Endpoint = new Uri(url.EndsWith('/') ? url : url + "/");
        }

        return new OpenAIClient(new ApiKeyCredential(Vacio(clave) ?? ClaveVacia), opciones).GetChatClient(modelo).AsIChatClient();
    }

    private static IChatClient AnthropicCliente(string? clave, string? endpoint, string modelo)
    {
        var cliente = new AnthropicClient { ApiKey = Vacio(clave) ?? throw new InvalidOperationException("IA:ApiKey es obligatoria con anthropic.") };
        if (Vacio(endpoint) is { } url)
        {
            cliente = new AnthropicClient { ApiKey = cliente.ApiKey, BaseUrl = url };
        }

        return cliente.AsIChatClient(modelo);
    }

    private static string? Vacio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
