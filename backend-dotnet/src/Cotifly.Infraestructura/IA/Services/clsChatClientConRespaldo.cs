using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.Exceptions;
using Cotifly.Aplicacion.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Decorador de <see cref="IChatClient"/> con proveedores de respaldo (<c>IA:Fallbacks</c>, ADR-003; equivalente a los
/// <c>fallbacks</c> de LiteLLM del Python): si un proveedor responde 429, un 5xx, falla la red o agota su tiempo, la misma
/// petición pasa al siguiente. Un error del cliente (400, 401...) o la cancelación del llamador se propagan sin probar otro, salvo
/// el 400 "No credentials for provider" de un gateway como OmniRoute (#84): el gateway no tiene credenciales utilizables para ese
/// proveedor (sin configurar o todas en enfriamiento), que es indisponibilidad y no un error de la petición.
/// </summary>
public sealed class clsChatClientConRespaldo : IChatClient
{
    /// <summary>Mensaje del 400 de OmniRoute cuando no tiene credenciales utilizables para el proveedor del modelo.</summary>
    public const string SinCredencialesDelGateway = "No credentials for provider";

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
            catch (Exception ex) when (EsRecuperable(ex, cancellationToken) && Estado(ex) != 429)
            {
                // HU-013.16 (#126): fallaron todos por indisponibilidad (no por cuota): el chat responde IA_NO_DISPONIBLE.
                _logger.LogWarning("Ningún proveedor de IA respondió: el último falló ({Tipo}, estado {Estado}).", ex.GetType().Name, Estado(ex)?.ToString() ?? "sin estado");
                throw new clsIaNoDisponibleException($"Fallaron los {_clientes.Count} proveedores de IA configurados.", ex);
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

    /// <summary>
    /// ¿Vale la pena probar otro proveedor? 429, 5xx, red caída o tiempo agotado (no la cancelación del llamador), y el 400 de un
    /// gateway sin credenciales para el proveedor (<see cref="SinCredencialesDelGateway"/>).
    /// </summary>
    /// <remarks>
    /// DEF-1027-1 (#226, E2E real del 2026-10-27): con el proveedor caído, el SDK de OpenAI agota sus reintentos y lanza un
    /// <see cref="AggregateException"/> ("Retry failed after 4 tries") con una <see cref="ClientResultException"/> sin estado HTTP por
    /// intento, que envuelve el <see cref="HttpRequestException"/> con la <see cref="System.Net.Sockets.SocketException"/>. Antes solo se
    /// miraba la excepción y su primera interna, así que eso no era indisponibilidad y el chat respondía <c>ERROR_INTERNO</c>. Ahora se
    /// recorre toda la cadena (también las internas de un <see cref="AggregateException"/>) buscando la red caída, el socket o el tiempo
    /// agotado.
    /// </remarks>
    public static bool EsRecuperable(Exception error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (cancellationToken.IsCancellationRequested && Cadena(error).Any(e => e is OperationCanceledException))
        {
            return false;
        }

        if (Estado(error) is { } estado)
        {
            // HU-013.16 (#126): también un 404 (ruta o modelo inexistente; el gateway caído respondía el 404 de Traefik) y un 408. Un
            // 400 de contenido no: el siguiente modelo recibiría la misma petición inválida.
            return estado is 404 or 408 or 429 || estado >= 500 || (estado == 400 && SinCredenciales(error));
        }

        return Cadena(error).Any(e => e is HttpRequestException or TimeoutException or TaskCanceledException or IOException
            or System.Net.Sockets.SocketException);
    }

    /// <summary>
    /// La excepción, sus internas y, en un <see cref="AggregateException"/>, todas sus internas (en profundidad, con un tope para no
    /// recorrer cadenas anómalas). El mismo tope tienen <see cref="Estado"/> y <see cref="SinCredenciales"/>: una cadena cíclica no
    /// cuelga el filtro de excepciones.
    /// </summary>
    private static IEnumerable<Exception> Cadena(Exception error)
    {
        var pendientes = new Stack<Exception>();
        pendientes.Push(error);
        for (var vistos = 0; pendientes.Count > 0 && vistos < MaximoExcepcionesRecorridas; vistos++)
        {
            var actual = pendientes.Pop();
            yield return actual;
            if (actual is AggregateException agregada)
            {
                foreach (var interna in agregada.InnerExceptions.Reverse())
                {
                    pendientes.Push(interna);
                }
            }
            else if (actual.InnerException is { } interna)
            {
                pendientes.Push(interna);
            }
        }
    }

    private const int MaximoExcepcionesRecorridas = clsEstadoHttpExcepcion.MaximoInternas;

    /// <summary>
    /// ¿El <c>error.message</c> del gateway EMPIEZA con <c>No credentials for provider:</c>? Se lee del cuerpo de la respuesta cruda de
    /// <see cref="ClientResultException"/> si está disponible; si no, del mensaje de la excepción (un cuerpo JSON o una de sus líneas,
    /// como arma el SDK de OpenAI: <c>HTTP 400 (...)</c>, línea vacía y el mensaje). Un 400 que solo cita la frase no cuenta.
    /// </summary>
    private static bool SinCredenciales(Exception error)
    {
        var revisadas = 0;
        for (var actual = error; actual is not null && revisadas < MaximoExcepcionesRecorridas; actual = actual.InnerException, revisadas++)
        {
            if (actual is ClientResultException resultado && MensajeDelCuerpo(CuerpoCrudo(resultado)) is { } delCuerpo)
            {
                return EmpiezaSinCredenciales(delCuerpo);
            }

            var mensaje = MensajeDelCuerpo(actual.Message) ?? actual.Message;
            if (mensaje.Split('\n').Any(linea => EmpiezaSinCredenciales(linea.Trim())))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EmpiezaSinCredenciales(string texto) => texto.StartsWith(SinCredencialesDelGateway + ":", StringComparison.Ordinal);

    private static string? CuerpoCrudo(ClientResultException error)
    {
        try
        {
            return error.GetRawResponse()?.Content?.ToString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary><c>error.message</c> de un cuerpo de error estilo OpenAI, o null si el texto no lo es.</summary>
    private static string? MensajeDelCuerpo(string? cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo) || cuerpo.TrimStart()[0] != '{')
        {
            return null;
        }

        try
        {
            using var documento = JsonDocument.Parse(cuerpo);
            return documento.RootElement.TryGetProperty("error", out var detalle) && detalle.ValueKind == JsonValueKind.Object
                   && detalle.TryGetProperty("message", out var mensaje) && mensaje.ValueKind == JsonValueKind.String
                ? mensaje.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Código HTTP de la excepción o de sus internas: <see cref="HttpRequestException.StatusCode"/> o una propiedad <c>Status</c> /
    /// <c>StatusCode</c> numérica (<c>ClientResultException</c> del SDK de OpenAI, excepciones del SDK de Anthropic).
    /// </summary>
    public static int? Estado(Exception? error) => clsEstadoHttpExcepcion.Estado(error);
}
