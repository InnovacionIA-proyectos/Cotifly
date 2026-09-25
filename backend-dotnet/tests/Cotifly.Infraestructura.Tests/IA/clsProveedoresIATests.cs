using System.Net;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Infraestructura.Tests.IA;

/// <summary>
/// Proveedores de IA (ADR-003): fábrica por <c>IA:Proveedor</c> sin red, validación de <c>clsOpcionesIA</c>, respaldo ante 429/5xx
/// (<see cref="clsChatClientConRespaldo"/>) y limitador de peticiones por minuto (port de <c>LimitadorRPM</c>).
/// </summary>
public sealed class clsProveedoresIATests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("openai", "gpt-4o-mini", "sk-clave-de-prueba-123456", null)]
    [InlineData("azure_openai", "gpt-4o", "clave-azure-de-prueba-123", "https://recurso.openai.azure.com")]
    [InlineData("openai_compatible", "openrouter/modelo", null, "https://openrouter.ai/api/v1")]
    [InlineData("gemini", "gemini-2.5-flash", "clave-gemini-de-prueba-1", null)]
    [InlineData("ollama", "llama3.1", null, null)]
    [InlineData("anthropic", "claude-sonnet-4-5", "sk-ant-clave-de-prueba-12", null)]
    public void LaFabricaConstruyeCadaProveedorSinLlamarALaRed(string proveedor, string modelo, string? clave, string? endpoint)
    {
        using var cliente = clsChatClientFactory.CrearProveedor(new clsOpcionesProveedorIA { Proveedor = proveedor, Modelo = modelo, ApiKey = clave, Endpoint = endpoint });

        cliente.Should().NotBeNull();
        cliente.GetService<ChatClientMetadata>()?.DefaultModelId.Should().Be(modelo);
    }

    [Theory]
    [InlineData("https://recurso.openai.azure.com", "https://recurso.openai.azure.com/openai/v1/")]
    [InlineData("https://recurso.openai.azure.com/", "https://recurso.openai.azure.com/openai/v1/")]
    [InlineData("https://recurso.openai.azure.com/openai/v1", "https://recurso.openai.azure.com/openai/v1/")]
    public void AzureOpenAiUsaSuApiV1(string endpoint, string esperado) => clsChatClientFactory.EndpointAzure(endpoint).Should().Be(esperado);

    [Fact]
    public async Task SinProveedorElAgenteNoEstaConfiguradoYFallaConUnMensajeClaro()
    {
        using var cliente = clsChatClientFactory.Crear(new clsOpcionesIA(), NullLoggerFactory.Instance, TimeProvider.System);

        cliente.Should().BeOfType<clsChatClientNoConfigurado>();
        var llamar = async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct);
        (await llamar.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*IA:Proveedor*");
    }

    [Fact]
    public void ConRespaldosYRpmLaFabricaEncadenaLosDecoradores()
    {
        var opciones = new clsOpcionesIA
        {
            Proveedor = "openai",
            Modelo = "gpt-4o-mini",
            ApiKey = "sk-clave-de-prueba-123456",
            Rpm = 8,
            Fallbacks = [new clsOpcionesProveedorIA { Proveedor = "ollama", Modelo = "llama3.1" }],
        };

        using var cliente = clsChatClientFactory.Crear(opciones, NullLoggerFactory.Instance, TimeProvider.System);

        cliente.GetService<clsChatClientLimitado>().Should().NotBeNull();
        cliente.GetService<clsChatClientConRespaldo>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("deepseek", "m", "k", null, "IA:Proveedor debe ser uno de")]
    [InlineData("openai", null, "sk-clave-de-prueba-123456", null, "IA:Modelo es obligatorio")]
    [InlineData("anthropic", "claude", null, null, "IA:ApiKey es obligatoria")]
    [InlineData("azure_openai", "gpt", "clave-azure-de-prueba-123", null, "IA:Endpoint es obligatorio")]
    [InlineData("openai_compatible", "m", null, "ftp://x", "IA:Endpoint debe ser una URL")]
    public void LaValidacionExigeLoQueNecesitaCadaProveedor(string proveedor, string? modelo, string? clave, string? endpoint, string error)
    {
        var validador = new clsValidadorOpcionesIA(new clsEntornoFalso("Development"));

        var resultado = validador.Validate(null, new clsOpcionesIA { Proveedor = proveedor, Modelo = modelo, ApiKey = clave, Endpoint = endpoint });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain(error);
    }

    [Fact]
    public void SinProveedorLaConfiguracionEsValidaPeroNoAdmiteRespaldos()
    {
        var validador = new clsValidadorOpcionesIA(new clsEntornoFalso("Production"));

        validador.Validate(null, new clsOpcionesIA()).Succeeded.Should().BeTrue();
        validador.Validate(null, new clsOpcionesIA { Fallbacks = [new clsOpcionesProveedorIA { Proveedor = "ollama", Modelo = "x" }] }).Failed.Should().BeTrue();
    }

    [Fact]
    public void EnProductionLaClaveDeUnRespaldoNoPuedeSerUnPlaceholder()
    {
        var opciones = new clsOpcionesIA
        {
            Proveedor = "ollama",
            Modelo = "llama3.1",
            Fallbacks = [new clsOpcionesProveedorIA { Proveedor = "openai", Modelo = "gpt", ApiKey = "<secreto>" }],
        };

        var resultado = new clsValidadorOpcionesIA(new clsEntornoFalso("Production")).Validate(null, opciones);

        resultado.FailureMessage.Should().Contain("IA:Fallbacks:0:ApiKey").And.NotContain("<secreto>");
    }

    // Respaldo (fallbacks de LiteLLM): 429 y 5xx pasan al siguiente proveedor; 400 y la cancelación no.
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task ElRespaldoSoloActuaAnteLimitesYFallosDelServidor(HttpStatusCode estado, bool usaRespaldo)
    {
        var principal = Substitute.For<IChatClient>();
        var respaldo = Substitute.For<IChatClient>();
        principal.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("fallo", null, estado));
        respaldo.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "respuesta del respaldo")));
        using var cliente = new clsChatClientConRespaldo([principal, respaldo], NullLogger<clsChatClientConRespaldo>.Instance);

        var llamar = async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct);

        if (usaRespaldo)
        {
            (await llamar()).Text.Should().Be("respuesta del respaldo");
        }
        else
        {
            await llamar.Should().ThrowAsync<HttpRequestException>();
            await respaldo.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task SiTodosLosProveedoresFallanSePropagaElUltimoError()
    {
        var principal = Substitute.For<IChatClient>();
        var respaldo = Substitute.For<IChatClient>();
        principal.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("caído", null, HttpStatusCode.BadGateway));
        respaldo.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("límite", null, HttpStatusCode.TooManyRequests));
        using var cliente = new clsChatClientConRespaldo([principal, respaldo], NullLogger<clsChatClientConRespaldo>.Instance);

        var llamar = async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct);

        (await llamar.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void ReconoceElEstadoHttpDeLasExcepcionesDeLosSdk()
    {
        clsChatClientConRespaldo.Estado(new clsErrorSdkFalso(429)).Should().Be(429);
        clsChatClientConRespaldo.Estado(new InvalidOperationException("x", new HttpRequestException("y", null, HttpStatusCode.BadGateway))).Should().Be(502);
        clsChatClientConRespaldo.EsRecuperable(new HttpRequestException("red caída"), Ct).Should().BeTrue();
        clsChatClientConRespaldo.EsRecuperable(new InvalidOperationException("argumento"), Ct).Should().BeFalse();
    }

    // test del LimitadorRPM: la segunda llamada dentro del minuto espera el intervalo (60 / rpm).
    [Fact]
    public async Task ElLimitadorEsperaElIntervaloEntreLlamadas()
    {
        var tiempo = new clsTiempoFalso();
        var esperas = new List<TimeSpan>();
        using var limitador = new clsLimitadorRpm(60, tiempo, (espera, _) =>
        {
            esperas.Add(espera);
            tiempo.Avanzar(espera);
            return Task.CompletedTask;
        });

        await limitador.EsperarAsync(Ct);
        await limitador.EsperarAsync(Ct);
        tiempo.Avanzar(TimeSpan.FromSeconds(5));
        await limitador.EsperarAsync(Ct);

        limitador.Intervalo.Should().Be(TimeSpan.FromSeconds(1));
        esperas.Should().Equal(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConRpmCeroElLimitadorNoEspera()
    {
        var esperas = 0;
        using var limitador = new clsLimitadorRpm(0, TimeProvider.System, (_, _) =>
        {
            esperas++;
            return Task.CompletedTask;
        });

        await limitador.EsperarAsync(Ct);
        await limitador.EsperarAsync(Ct);

        esperas.Should().Be(0);
    }

    private sealed class clsErrorSdkFalso(int status) : Exception("error del SDK")
    {
        public int Status { get; } = status;
    }

    private sealed class clsTiempoFalso : TimeProvider
    {
        private DateTimeOffset _ahora = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _ahora;

        public void Avanzar(TimeSpan lapso) => _ahora += lapso;
    }
}
