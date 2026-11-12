using System.ClientModel.Primitives;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;

namespace Cotifly.Infraestructura.Tests.IA;

/// <summary>
/// Proveedores OpenAI-compatible como OmniRoute, con un <see cref="HttpMessageHandler"/> falso (sin red):
/// <c>"stream": false</c> explícito con <c>openai_compatible</c> (<see cref="clsPoliticaSinStreaming"/>), el resto de proveedores sin
/// cambios, y el contrato de lo que viaja en <c>tools</c>: las declaraciones portables, sin <c>condition</c> ni tipos en arreglo.
/// </summary>
public sealed class clsOpenAiCompatibleTests
{
    private const string Endpoint = "http://omniroute-prueba.local/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConOpenAiCompatibleLaPeticionLlevaStreamFalseExplicitoYSeLeeLaRespuestaJson()
    {
        var http = new clsProveedorFalso().ResponderJson(RespuestaTexto("hola"));
        using var cliente = Cliente(clsOpcionesIA.OpenAiCompatible, http);

        var respuesta = await cliente.GetResponseAsync("di hola", new ChatOptions { MaxOutputTokens = 1500 }, Ct);

        respuesta.Text.Should().Be("hola");
        var cuerpo = http.Cuerpos.Should().ContainSingle().Subject;
        cuerpo["stream"]!.GetValue<bool>().Should().BeFalse();
        cuerpo["model"]!.GetValue<string>().Should().Be("gemini/gemini-3.5-flash");
        cuerpo["max_completion_tokens"]!.GetValue<int>().Should().Be(1500);
        http.Rutas.Should().ContainSingle().Which.Should().Be("/v1/chat/completions");
    }

    // Comportamiento idéntico para los demás proveedores del SDK de OpenAI: el cuerpo no cambia.
    [Theory]
    [InlineData(clsOpcionesIA.OpenAi)]
    [InlineData(clsOpcionesIA.Gemini)]
    [InlineData(clsOpcionesIA.Ollama)]
    public async Task LosDemasProveedoresNoCambianElCuerpo(string proveedor)
    {
        var http = new clsProveedorFalso().ResponderJson(RespuestaTexto("hola"));
        using var cliente = Cliente(proveedor, http);

        await cliente.GetResponseAsync("di hola", cancellationToken: Ct);

        http.Cuerpos.Should().ContainSingle().Which.ContainsKey("stream").Should().BeFalse();
    }

    // Por qué hace falta la política: sin "stream": false OmniRoute responde SSE y el SDK no lo puede leer como chat.completion.
    [Fact]
    public async Task SinStreamFalseUnCuerpoSseRompeElSdkDeOpenAi()
    {
        var http = new clsProveedorFalso().ResponderSse(EventosSse("hola"));
        using var cliente = Cliente(clsOpcionesIA.OpenAi, http);

        var llamar = async () => await cliente.GetResponseAsync("di hola", cancellationToken: Ct);

        await llamar.Should().ThrowAsync<Exception>();
    }

    // El modo streaming ya trae "stream": true y la política no lo toca.
    [Fact]
    public async Task UnaPeticionEnStreamingConservaStreamTrue()
    {
        var http = new clsProveedorFalso().ResponderSse(EventosSse("hola"));
        using var cliente = Cliente(clsOpcionesIA.OpenAiCompatible, http);

        var texto = new StringBuilder();
        await foreach (var parte in cliente.GetStreamingResponseAsync("di hola", cancellationToken: Ct))
        {
            texto.Append(parte.Text);
        }

        texto.ToString().Should().Be("hola");
        http.Cuerpos.Should().ContainSingle().Which["stream"]!.GetValue<bool>().Should().BeTrue();
    }

    // Contrato de lo que viaja: la declaración portable llega con la descripción de la función y sin claves que Gemini rechaza.
    [Fact]
    public async Task LasHerramientasViajanConEsquemaPortableYLaDescripcionEnLaFuncion()
    {
        var http = new clsProveedorFalso().ResponderJson(RespuestaConHerramienta());
        using var cliente = Cliente(clsOpcionesIA.OpenAiCompatible, http);
        var funcion = KernelFunctionFactory.CreateFromMethod(
            ([Description("Ciudad de origen.")] string origen, [Description("Peso en kilogramos.")] decimal? pesoKg = null) => origen,
            "buscar_tarifa",
            "Busca la tarifa de costo de la ruta.");

        var respuesta = await cliente.GetResponseAsync(
            "Cotiza Bogotá", new ChatOptions { Tools = clsEsquemaPortableHerramientas.Declarar([funcion]), ToolMode = ChatToolMode.Auto }, Ct);

        var llamada = respuesta.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        llamada.Name.Should().Be("buscar_tarifa");
        var herramienta = http.Cuerpos.Should().ContainSingle().Subject["tools"]!.AsArray().Should().ContainSingle().Subject!["function"]!;
        herramienta["name"]!.GetValue<string>().Should().Be("buscar_tarifa");
        herramienta["description"]!.GetValue<string>().Should().Be("Busca la tarifa de costo de la ruta.");
        var parametros = herramienta["parameters"]!.AsObject();
        parametros.ContainsKey("condition").Should().BeFalse();
        parametros["properties"]!["pesoKg"]!["type"]!.GetValue<string>().Should().Be("number");
        parametros["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("origen");
        parametros.Select(p => p.Key).Should().BeSubsetOf(
            clsEsquemaPortableHerramientas.PalabrasPermitidas.Append("additionalProperties"),
            "el adaptador de OpenAI de Microsoft.Extensions.AI solo agrega additionalProperties en la raíz");
    }

    [Theory]
    [InlineData("GET", "http://x/v1/chat/completions", "{\"a\":1}")]
    [InlineData("POST", "http://x/v1/embeddings", "{\"a\":1}")]
    [InlineData("POST", "http://x/v1/chat/completions", "no es json")]
    [InlineData("POST", "http://x/v1/chat/completions", "[1,2]")]
    [InlineData("POST", "http://x/v1/chat/completions", "{\"stream\":true}")]
    public void LaPoliticaNoTocaLoQueNoEsUnChatSinStream(string metodo, string url, string cuerpo)
    {
        using var mensaje = new HttpClientPipelineTransport().CreateMessage();
        mensaje.Request.Method = metodo;
        mensaje.Request.Uri = new Uri(url);
        var original = System.ClientModel.BinaryContent.Create(BinaryData.FromString(cuerpo));
        mensaje.Request.Content = original;

        clsPoliticaSinStreaming.Preparar(mensaje.Request);

        mensaje.Request.Content.Should().BeSameAs(original);
    }

    private static IChatClient Cliente(string proveedor, clsProveedorFalso http) =>
        clsChatClientFactory.CrearProveedor(
            new clsOpcionesProveedorIA { Proveedor = proveedor, Modelo = "gemini/gemini-3.5-flash", ApiKey = "clave-de-prueba-0123456789", Endpoint = Endpoint },
            30,
            new HttpClient(http));

    private static string RespuestaTexto(string texto) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-prueba",
        @object = "chat.completion",
        created = 1_790_000_000,
        model = "gemini/gemini-3.5-flash",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = texto }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 3, completion_tokens = 1, total_tokens = 4 },
    });

    private static string RespuestaConHerramienta() => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-prueba",
        @object = "chat.completion",
        created = 1_790_000_000,
        model = "gemini/gemini-3.5-flash",
        choices = new[]
        {
            new
            {
                index = 0,
                message = new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "buscar_tarifa", arguments = "{\"origen\":\"Bogotá\"}" } } },
                },
                finish_reason = "tool_calls",
            },
        },
    });

    private static string EventosSse(string texto) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-prueba",
            @object = "chat.completion.chunk",
            created = 1_790_000_000,
            model = "gemini/gemini-3.5-flash",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = texto }, finish_reason = (string?)null } },
        }) + "\n\n"
        + "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-prueba",
            @object = "chat.completion.chunk",
            created = 1_790_000_000,
            model = "gemini/gemini-3.5-flash",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
        }) + "\n\ndata: [DONE]\n\n";

    /// <summary>Endpoint OpenAI-compatible falso: guarda el cuerpo y la ruta de cada petición y responde lo encolado.</summary>
    private sealed class clsProveedorFalso : HttpMessageHandler
    {
        private readonly Queue<(string Cuerpo, string Tipo)> _respuestas = new();

        public List<JsonObject> Cuerpos { get; } = [];

        public List<string> Rutas { get; } = [];

        public clsProveedorFalso ResponderJson(string json)
        {
            _respuestas.Enqueue((json, "application/json"));
            return this;
        }

        public clsProveedorFalso ResponderSse(string eventos)
        {
            _respuestas.Enqueue((eventos, "text/event-stream"));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Rutas.Add(request.RequestUri!.AbsolutePath);
            Cuerpos.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            var (cuerpo, tipo) = _respuestas.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cuerpo, Encoding.UTF8, tipo) };
        }
    }
}
