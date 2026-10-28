using System.ClientModel;
using System.Net.Sockets;
using Cotifly.Aplicacion.Agente.Exceptions;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.IA;

/// <summary>
/// DEF-1027-1 (#226, E2E real del 2026-10-27): con el proveedor de IA caído, el SDK de OpenAI reintenta y, al agotar los intentos,
/// lanza un <see cref="AggregateException"/> ("Retry failed after 4 tries") cuyas internas son <see cref="ClientResultException"/> sin
/// estado HTTP que envuelven el <see cref="HttpRequestException"/> con la <see cref="SocketException"/> ("Connection refused"). Antes no
/// se reconocía como indisponibilidad y el chat respondía <c>ERROR_INTERNO</c>; ahora la red caída, el socket y el tiempo agotado,
/// también dentro de un <see cref="AggregateException"/>, son <c>IA_NO_DISPONIBLE</c>. La cancelación del llamador sigue sin serlo.
/// </summary>
public sealed class clsIaCaidaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestException ConexionRechazada() =>
        new("Connection refused (127.0.0.1:1)", new SocketException((int)SocketError.ConnectionRefused));

    public static TheoryData<string, Exception> FallosDeRed() => new()
    {
        { "HttpRequestException con SocketException", ConexionRechazada() },
        { "SocketException sola", new SocketException((int)SocketError.ConnectionRefused) },
        { "SocketException dentro de otra excepción", new InvalidOperationException("envoltura", new SocketException((int)SocketError.HostUnreachable)) },
        { "reintentos agotados del SDK", new AggregateException("Retry failed after 4 tries.",
            Enumerable.Range(0, 4).Select(_ => new ClientResultException("Connection refused (127.0.0.1:1)", null, ConexionRechazada()))) },
        { "tiempo agotado", new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout.", new TimeoutException()) },
        { "tiempo agotado tras los reintentos", new AggregateException("Retry failed after 4 tries.",
            new TaskCanceledException("timeout", new TimeoutException()), new TaskCanceledException("timeout", new TimeoutException())) },
        { "TimeoutException", new TimeoutException("sin respuesta") },
    };

    [Theory]
    [MemberData(nameof(FallosDeRed))]
    public async Task UnFalloDeRedDeTodosLosProveedoresEsIaNoDisponible(string caso, Exception fallo)
    {
        var unico = new clsClienteQueFalla(fallo);
        using var cliente = new clsChatClientConRespaldo([unico], NullLogger<clsChatClientConRespaldo>.Instance);

        var error = (await ((Func<Task>)(async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct)))
            .Should().ThrowAsync<clsIaNoDisponibleException>(caso)).Which;

        error.InnerException.Should().BeSameAs(fallo);
        clsTextosAgente.CodigoDeFallo(error).Should().Be(clsTextosAgente.CodigoIaNoDisponible, caso);
        clsTextosAgente.ParaFallo(error).Should().Be(clsTextosAgente.IaNoDisponible, caso);
    }

    [Theory]
    [MemberData(nameof(FallosDeRed))]
    public async Task UnFalloDeRedDelPrincipalPasaAlRespaldo(string caso, Exception fallo)
    {
        var respaldo = new clsClienteQueResponde("desde el respaldo");
        using var cliente = new clsChatClientConRespaldo([new clsClienteQueFalla(fallo), respaldo], NullLogger<clsChatClientConRespaldo>.Instance);

        (await cliente.GetResponseAsync("hola", cancellationToken: Ct)).Text.Should().Be("desde el respaldo", caso);
    }

    [Fact]
    public void LaCancelacionDelLlamadorYUnErrorQueNoEsDeRedNoSonIndisponibilidad()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();

        clsChatClientConRespaldo.EsRecuperable(new AggregateException(new TaskCanceledException("cancelada")), cancelado.Token).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new TaskCanceledException("cancelada"), cancelado.Token).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new AggregateException(new InvalidOperationException("argumento")), Ct).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new AggregateException(new ArgumentException("x")), Ct).Should().BeFalse();
    }

    /// <summary>
    /// Validación de #226: un error del cliente (credenciales malas, 401/403, o un 400 de contenido) sigue sin ser indisponibilidad
    /// aunque llegue dentro de un <see cref="AggregateException"/> o con un socket en su cadena: el código HTTP manda sobre la red.
    /// </summary>
    [Fact]
    public void UnErrorDelClienteNoEsIndisponibilidadNiDentroDeUnAggregateException()
    {
        static HttpRequestException Http(System.Net.HttpStatusCode codigo) =>
            new($"HTTP {(int)codigo}", new SocketException((int)SocketError.ConnectionReset), codigo);

        clsChatClientConRespaldo.EsRecuperable(Http(System.Net.HttpStatusCode.Unauthorized), Ct).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new AggregateException(Http(System.Net.HttpStatusCode.Unauthorized)), Ct).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new AggregateException(Http(System.Net.HttpStatusCode.Forbidden)), Ct).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new InvalidOperationException("envoltura", Http(System.Net.HttpStatusCode.BadRequest)), Ct).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new AggregateException(Http(System.Net.HttpStatusCode.ServiceUnavailable)), Ct).Should().BeTrue("un 503 sí");
    }

    /// <summary>
    /// Validación de #226: la cancelación del llamador no salta al respaldo aunque venga envuelta (en otra excepción o en un
    /// <see cref="AggregateException"/> junto a un fallo de red), y con dos proveedores el respaldo no recibe la petición.
    /// </summary>
    [Fact]
    public async Task LaCancelacionDelLlamadorEnvueltaNoPasaAlRespaldo()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();

        clsChatClientConRespaldo.EsRecuperable(new InvalidOperationException("envoltura", new OperationCanceledException(cancelado.Token)), cancelado.Token)
            .Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(new AggregateException(ConexionRechazada(), new TaskCanceledException("cancelada")), cancelado.Token)
            .Should().BeFalse();

        var respaldo = new clsClienteQueResponde("no debía responder");
        var fallo = new AggregateException(new TaskCanceledException("cancelada", null, cancelado.Token));
        using var cliente = new clsChatClientConRespaldo([new clsClienteQueFalla(fallo), respaldo], NullLogger<clsChatClientConRespaldo>.Instance);

        (await ((Func<Task>)(async () => await cliente.GetResponseAsync("hola", cancellationToken: cancelado.Token)))
            .Should().ThrowAsync<AggregateException>()).Which.Should().BeSameAs(fallo);
        respaldo.Llamadas.Should().Be(0);
    }

    /// <summary>
    /// Validación de #226: una cadena de internas cíclica (solo se puede armar por reflexión) o muy profunda no cuelga el filtro: el
    /// recorrido tiene tope en <see cref="EsRecuperable"/>, en el código HTTP y en la lectura del 400 del gateway.
    /// </summary>
    [Fact]
    public async Task UnaCadenaCiclicaOMuyProfundaNoCuelgaLaClasificacion()
    {
        var segunda = new InvalidOperationException("segunda");
        var primera = new InvalidOperationException("primera", segunda);
        typeof(Exception).GetField("_innerException", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(segunda, primera);
        var agregada = new AggregateException(primera, new AggregateException(primera));

        // Sin tope, WaitAsync agota los 10 s (TimeoutException) en vez de colgar la suite.
        var resultado = await Task.Run(() => (
            clsChatClientConRespaldo.EsRecuperable(primera, Ct),
            clsChatClientConRespaldo.EsRecuperable(agregada, Ct),
            clsChatClientConRespaldo.Estado(primera)), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        resultado.Should().Be((false, false, (int?)null));

        Exception profunda = new SocketException((int)SocketError.ConnectionRefused);
        for (var i = 0; i < 200; i++)
        {
            profunda = new InvalidOperationException("nivel " + i, profunda);
        }

        clsChatClientConRespaldo.EsRecuperable(profunda, Ct).Should().BeFalse("más allá del tope no se busca la red caída");
    }

    // De punta a punta con el SDK real (openai_compatible, el de OmniRoute) y un transporte que rechaza la conexión: los reintentos
    // del SDK terminan en IA_NO_DISPONIBLE, no en ERROR_INTERNO.
    [Fact]
    public async Task ConElSdkRealYLaConexionRechazadaElTurnoEsIaNoDisponible()
    {
        var manejador = new clsManejadorSinConexion();
        using var http = new HttpClient(manejador);
        var proveedor = clsChatClientFactory.CrearProveedor(
            new clsOpcionesProveedorIA { Proveedor = clsOpcionesIA.OpenAiCompatible, Modelo = "modelo-caido", Endpoint = "http://127.0.0.1:1/v1" }, 5, http);
        using var cliente = new clsChatClientConRespaldo([proveedor], NullLogger<clsChatClientConRespaldo>.Instance);

        var error = (await ((Func<Task>)(async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct)))
            .Should().ThrowAsync<clsIaNoDisponibleException>()).Which;

        manejador.Peticiones.Should().BeGreaterThan(1, "el SDK reintenta antes de rendirse");
        clsTextosAgente.CodigoDeFallo(error).Should().Be(clsTextosAgente.CodigoIaNoDisponible, error.InnerException?.ToString());
    }

    private sealed class clsManejadorSinConexion : HttpMessageHandler
    {
        public int Peticiones { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Peticiones++;
            throw ConexionRechazada();
        }
    }

    private sealed class clsClienteQueFalla(Exception fallo) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(fallo);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class clsClienteQueResponde(string texto) : IChatClient
    {
        public int Llamadas { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, texto)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
