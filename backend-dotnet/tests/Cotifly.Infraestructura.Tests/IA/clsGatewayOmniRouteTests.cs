using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.IA;

/// <summary>
/// Endurecimiento de la integración con OmniRoute (HU-003.6, issue #84; hallazgos del validador): respaldo solo ante el 400 cuyo
/// <c>error.message</c> empieza con "No credentials for provider:", 429 de enfriamiento sin reintentos del SDK, casos límite de
/// <see cref="clsPoliticaSinStreaming"/> y respaldos vacíos en la configuración. Con el SDK real de OpenAI y un manejador falso.
/// </summary>
public sealed class clsGatewayOmniRouteTests
{
    private const string SinCredenciales = "{\"error\":{\"message\":\"No credentials for provider: gemini\",\"type\":\"invalid_request_error\",\"code\":400}}";
    private const string Enfriamiento = "{\"error\":{\"message\":\"All credentials for model gemini-3.5-flash are cooling down\",\"type\":\"rate_limit_error\",\"code\":\"model_cooldown\",\"model\":\"gemini-3.5-flash\",\"reset_seconds\":33311}}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- Respaldo ante "No credentials for provider" ----------

    // F4: la excepción que lanza el SDK real con el 400 de OmniRoute pasa al respaldo, con openai_compatible y con openai.
    [Theory]
    [InlineData(clsOpcionesIA.OpenAiCompatible)]
    [InlineData(clsOpcionesIA.OpenAi)]
    public async Task El400SinCredencialesDelSdkRealEsRecuperable(string proveedor)
    {
        var http = new clsManejador().Responder(HttpStatusCode.BadRequest, SinCredenciales);
        using var cliente = Cliente(proveedor, http);

        var error = await Capturar(() => cliente.GetResponseAsync("hola", cancellationToken: Ct));

        clsChatClientConRespaldo.EsRecuperable(error, Ct).Should().BeTrue($"{error.GetType().Name}: {error.Message}");
    }

    // F3: una ClientResultException con el cuerpo crudo de OmniRoute.
    [Fact]
    public void UnaClientResultExceptionConElCuerpoDelGatewayEsRecuperable()
    {
        using var respuesta = new clsRespuestaFalsa(400, SinCredenciales);

        clsChatClientConRespaldo.EsRecuperable(new ClientResultException(respuesta), Ct).Should().BeTrue();
    }

    // F1 y F2: un 400 legítimo que solo cita la frase (en el cuerpo crudo o en el mensaje), o un 401, no pasan al respaldo.
    [Fact]
    public async Task Un400QueSoloCitaLaFraseNoEsRecuperable()
    {
        var cita = "{\"error\":{\"message\":\"Invalid value at 'messages[1].content' (\\\"No credentials for provider: gemini\\\")\",\"type\":\"invalid_request_error\"}}";
        var http = new clsManejador().Responder(HttpStatusCode.BadRequest, cita);
        using var cliente = Cliente(clsOpcionesIA.OpenAiCompatible, http);
        var delSdk = await Capturar(() => cliente.GetResponseAsync("hola", cancellationToken: Ct));

        clsChatClientConRespaldo.EsRecuperable(delSdk, Ct).Should().BeFalse(delSdk.Message);
        clsChatClientConRespaldo.EsRecuperable(
            new HttpRequestException("Invalid value at 'messages[1].content' (\"No credentials for provider\")", null, HttpStatusCode.BadRequest), Ct).Should().BeFalse();
        clsChatClientConRespaldo.EsRecuperable(
            new HttpRequestException("No credentials for provider: gemini", null, HttpStatusCode.Unauthorized), Ct).Should().BeFalse();
    }

    // De punta a punta: con respaldo configurado, el 400 sin credenciales del principal lo atiende el respaldo.
    [Fact]
    public async Task ConRespaldoEl400SinCredencialesLoAtiendeElRespaldo()
    {
        var principal = new clsManejador().Responder(HttpStatusCode.BadRequest, SinCredenciales);
        var respaldo = new clsManejador().Responder(HttpStatusCode.OK, RespuestaTexto("desde el respaldo"));
        using var cliente = new clsChatClientConRespaldo(
            [Cliente(clsOpcionesIA.OpenAiCompatible, principal), Cliente(clsOpcionesIA.OpenAiCompatible, respaldo)],
            NullLogger<clsChatClientConRespaldo>.Instance);

        (await cliente.GetResponseAsync("hola", cancellationToken: Ct)).Text.Should().Be("desde el respaldo");
    }

    // ---------- 429 de enfriamiento ----------

    // Un 429 model_cooldown (reset de horas) no se reintenta: una sola petición y el error sale de inmediato hacia el respaldo.
    [Fact]
    public async Task Un429DeEnfriamientoNoSeReintenta()
    {
        var http = new clsManejador().Responder(HttpStatusCode.TooManyRequests, Enfriamiento, veces: 5);
        using var cliente = Cliente(clsOpcionesIA.OpenAiCompatible, http);
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var error = await Capturar(() => cliente.GetResponseAsync("hola", cancellationToken: Ct));

        reloj.Stop();
        http.Peticiones.Should().Be(1, "el SDK no reintenta un enfriamiento");
        clsChatClientConRespaldo.EsRecuperable(error, Ct).Should().BeTrue("un 429 pasa al respaldo");
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // Un 429 corriente (o con reset_seconds menor que el timeout) sigue con los reintentos del SDK.
    [Fact]
    public async Task Un429CorrienteSeReintenta()
    {
        var http = new clsManejador()
            .Responder(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Rate limit\",\"type\":\"rate_limit_error\",\"reset_seconds\":1}}")
            .Responder(HttpStatusCode.OK, RespuestaTexto("hola"));
        using var cliente = Cliente(clsOpcionesIA.OpenAiCompatible, http);

        (await cliente.GetResponseAsync("hola", cancellationToken: Ct)).Text.Should().Be("hola");
        http.Peticiones.Should().Be(2);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"model_cooldown\"}}", 60, true)]
    [InlineData("{\"error\":{\"code\":\"rate_limit\",\"reset_seconds\":120}}", 60, true)]
    [InlineData("{\"error\":{\"code\":\"rate_limit\",\"reset_seconds\":30}}", 60, false)]
    [InlineData("{\"error\":{\"reset_seconds\":\"mucho\"}}", 60, false)]
    [InlineData("{\"error\":\"texto\"}", 60, false)]
    [InlineData("no es json", 60, false)]
    [InlineData("", 60, false)]
    public void ReconoceElCuerpoDeEnfriamiento(string cuerpo, int timeout, bool esperado) =>
        clsPoliticaReintentoGateway.EsCuerpoDeEnfriamiento(cuerpo, timeout).Should().Be(esperado);

    // ---------- clsPoliticaSinStreaming: casos límite ----------

    [Fact]
    public void ConservaElTextoNoAsciiYLosNumeros()
    {
        const string Json = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"Bogotá → Medellín 🚚 <b>&'+\"}],\"temperature\":0.10000000000000001}";
        using var mensaje = Mensaje(Encoding.UTF8.GetBytes(Json));

        clsPoliticaSinStreaming.Preparar(mensaje.Request);

        var nuevo = JsonNode.Parse(Leer(mensaje.Request))!.AsObject();
        nuevo["stream"]!.GetValue<bool>().Should().BeFalse();
        nuevo["messages"]![0]!["content"]!.GetValue<string>().Should().Be("Bogotá → Medellín 🚚 <b>&'+");
        Leer(mensaje.Request).Should().Contain("0.10000000000000001");
    }

    [Theory]
    [InlineData("{\"model\":\"m\",\"stream\":null}", "http://x/v1/chat/completions")]
    [InlineData("{\"model\":\"m\"}", "http://x/v1/chat/completions?api-version=1")]
    public void StreamNullOUnaQuerySeTratanComoSinStream(string cuerpo, string url)
    {
        using var mensaje = Mensaje(Encoding.UTF8.GetBytes(cuerpo), url);

        clsPoliticaSinStreaming.Preparar(mensaje.Request);

        JsonNode.Parse(Leer(mensaje.Request))!["stream"]!.GetValue<bool>().Should().BeFalse();
    }

    // Un cuerpo con más de 64 niveles (el tope por defecto de System.Text.Json) también lleva stream:false.
    [Fact]
    public void UnCuerpoProfundoTambienLlevaStreamFalse()
    {
        var json = "{\"model\":\"m\",\"x\":" + new string('[', 70) + new string(']', 70) + "}";
        using var mensaje = Mensaje(Encoding.UTF8.GetBytes(json));

        clsPoliticaSinStreaming.Preparar(mensaje.Request);

        JsonNode.Parse(Leer(mensaje.Request), documentOptions: new JsonDocumentOptions { MaxDepth = 200 })!["stream"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void UnCuerpoVacioOConUtf8InvalidoNoLanza()
    {
        var invalido = Encoding.UTF8.GetBytes("{\"a\":\"X\"}");
        invalido[6] = 0xFF;
        foreach (var cuerpo in new[] { Array.Empty<byte>(), invalido })
        {
            using var mensaje = Mensaje(cuerpo);
            var preparar = () => clsPoliticaSinStreaming.Preparar(mensaje.Request);
            preparar.Should().NotThrow();
        }
    }

    // ---------- Respaldos vacíos en la configuración (manifiestos con ${...:-}) ----------

    [Fact]
    public void UnRespaldoVacioNoExisteYLaConfiguracionEsValida()
    {
        var opciones = new clsOpcionesIA
        {
            Proveedor = clsOpcionesIA.OpenAiCompatible,
            Modelo = "gemini/gemini-3.5-flash-lite",
            Endpoint = "http://omniroute-prueba.local/v1",
            Fallbacks = [new clsOpcionesProveedorIA { Proveedor = "", Modelo = "", Endpoint = "", ApiKey = "" }, new clsOpcionesProveedorIA()],
        };

        new clsValidadorOpcionesIA(new clsEntornoFalso("Production")).Validate(null, opciones).Succeeded.Should().BeTrue();
        opciones.FallbacksConfigurados().Should().BeEmpty();
        using var cliente = clsChatClientFactory.Crear(opciones, NullLoggerFactory.Instance, TimeProvider.System);
        cliente.GetService<clsChatClientConRespaldo>().Should().BeNull("sin respaldos configurados no hay cadena de respaldo");
    }

    [Fact]
    public void UnRespaldoConModeloPeroSinProveedorEsUnError()
    {
        var opciones = new clsOpcionesIA
        {
            Proveedor = clsOpcionesIA.OpenAiCompatible,
            Modelo = "gemini/gemini-3.5-flash-lite",
            Endpoint = "http://omniroute-prueba.local/v1",
            Fallbacks = [new clsOpcionesProveedorIA { Modelo = "gemini/gemini-3.5-flash" }],
        };

        var resultado = new clsValidadorOpcionesIA(new clsEntornoFalso("Development")).Validate(null, opciones);

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("IA:Fallbacks:0:Proveedor es obligatorio");
    }

    private static IChatClient Cliente(string proveedor, clsManejador http) =>
        clsChatClientFactory.CrearProveedor(
            new clsOpcionesProveedorIA { Proveedor = proveedor, Modelo = "gemini/gemini-3.5-flash-lite", ApiKey = "clave-de-prueba-0123456789", Endpoint = "http://omniroute-prueba.local/v1" },
            30,
            new HttpClient(http));

    private static async Task<Exception> Capturar(Func<Task> accion)
    {
        try
        {
            await accion();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Se esperaba una excepción.");
    }

    private static string RespuestaTexto(string texto) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-prueba",
        @object = "chat.completion",
        created = 1_790_000_000,
        model = "gemini/gemini-3.5-flash-lite",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = texto }, finish_reason = "stop" } },
    });

    private static PipelineMessage Mensaje(byte[] cuerpo, string url = "http://x/v1/chat/completions")
    {
        var mensaje = new HttpClientPipelineTransport().CreateMessage();
        mensaje.Request.Method = "POST";
        mensaje.Request.Uri = new Uri(url);
        mensaje.Request.Content = BinaryContent.Create(BinaryData.FromBytes(cuerpo));
        return mensaje;
    }

    private static string Leer(PipelineRequest peticion)
    {
        using var copia = new MemoryStream();
        peticion.Content!.WriteTo(copia);
        return Encoding.UTF8.GetString(copia.ToArray());
    }

    /// <summary>Endpoint falso: responde lo encolado (la última respuesta se repite) y cuenta las peticiones.</summary>
    private sealed class clsManejador : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode Estado, string Cuerpo)> _respuestas = [];

        public int Peticiones { get; private set; }

        public clsManejador Responder(HttpStatusCode estado, string cuerpo, int veces = 1)
        {
            for (var i = 0; i < veces; i++)
            {
                _respuestas.Add((estado, cuerpo));
            }

            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (estado, cuerpo) = _respuestas[Math.Min(Peticiones, _respuestas.Count - 1)];
            Peticiones++;
            return Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class clsRespuestaFalsa(int estado, string cuerpo) : PipelineResponse
    {
        private readonly BinaryData _contenido = BinaryData.FromString(cuerpo);

        public override int Status => estado;

        public override string ReasonPhrase => "Bad Request";

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => _contenido;

        protected override PipelineResponseHeaders HeadersCore => new clsCabeceras();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _contenido;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_contenido);

        public override void Dispose()
        {
        }
    }

    private sealed class clsCabeceras : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield return new("Content-Type", "application/json");
        }

        public override bool TryGetValue(string name, out string? value)
        {
            value = name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ? "application/json" : null;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = TryGetValue(name, out var valor) ? [valor!] : null;
            return values is not null;
        }
    }
}
