using System.Net;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
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
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
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

    // OmniRoute responde 400 "No credentials for provider: gemini" cuando todas sus credenciales del proveedor están en
    // enfriamiento: es indisponibilidad, pasa al respaldo. Cualquier otro 400 no.
    [Theory]
    [InlineData("No credentials for provider: gemini", true)]
    [InlineData("Invalid JSON payload received. Unknown name \"condition\"", false)]
    public async Task UnGatewaySinCredencialesPasaAlRespaldo(string mensaje, bool usaRespaldo)
    {
        var principal = Substitute.For<IChatClient>();
        var respaldo = Substitute.For<IChatClient>();
        principal.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("HTTP 400", new HttpRequestException(mensaje, null, HttpStatusCode.BadRequest)));
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
            await llamar.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    // El gateway caído responde el 404 de Traefik: ese 404 pasa al
    // siguiente modelo y, si fallan todos por indisponibilidad, el chat responde IA_NO_DISPONIBLE con su texto propio.
    [Fact]
    public async Task UnPrincipalCon404PruebaTodosLosRespaldosYSiFallanEsIaNoDisponible()
    {
        var modelos = Enumerable.Range(0, 3).Select(_ => Substitute.For<IChatClient>()).ToList();
        modelos[0].GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("404 page not found", null, HttpStatusCode.NotFound));
        modelos[1].GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout));
        modelos[2].GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("caído", null, HttpStatusCode.BadGateway));
        using var cliente = new clsChatClientConRespaldo(modelos, NullLogger<clsChatClientConRespaldo>.Instance);

        var llamar = async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct);

        var error = (await llamar.Should().ThrowAsync<Cotifly.Aplicacion.Agente.Exceptions.clsIaNoDisponibleException>()).Which;
        error.InnerException.Should().BeOfType<HttpRequestException>();
        foreach (var modelo in modelos)
        {
            await modelo.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        }

        Cotifly.Aplicacion.Agente.Services.clsTextosAgente.ParaFallo(error).Should().Be(Cotifly.Aplicacion.Agente.Services.clsTextosAgente.IaNoDisponible);
        Cotifly.Aplicacion.Agente.Services.clsTextosAgente.CodigoDeFallo(error).Should().Be("IA_NO_DISPONIBLE");
    }

    [Fact]
    public async Task UnSoloModeloCaidoTambienEsIaNoDisponibleYUn400DeContenidoNo()
    {
        var unico = Substitute.For<IChatClient>();
        unico.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));
        using var cliente = new clsChatClientConRespaldo([unico], NullLogger<clsChatClientConRespaldo>.Instance);
        await ((Func<Task>)(async () => await cliente.GetResponseAsync("hola", cancellationToken: Ct)))
            .Should().ThrowAsync<Cotifly.Aplicacion.Agente.Exceptions.clsIaNoDisponibleException>();

        var contenido = Substitute.For<IChatClient>();
        contenido.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Invalid JSON payload", null, HttpStatusCode.BadRequest));
        using var otro = new clsChatClientConRespaldo([contenido], NullLogger<clsChatClientConRespaldo>.Instance);
        var error = (await ((Func<Task>)(async () => await otro.GetResponseAsync("hola", cancellationToken: Ct))).Should().ThrowAsync<HttpRequestException>()).Which;
        Cotifly.Aplicacion.Agente.Services.clsTextosAgente.CodigoDeFallo(error).Should().Be("ERROR_INTERNO");
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

    // Ventana deslizante. Las llamadas seguidas de un turno pasan sin espera mientras el minuto tenga cupo; la que
    // pasa del tope espera a que la más vieja salga de la ventana (no hay un intervalo fijo de 60/rpm entre todas las llamadas).
    [Fact]
    public async Task ElLimitadorDejaPasarLasLlamadasDelMinutoYEsperaSoloAlPasarDelTope()
    {
        var tiempo = new clsTiempoFalso();
        var esperas = new List<TimeSpan>();
        using var limitador = new clsLimitadorRpm(3, tiempo, (espera, _) =>
        {
            esperas.Add(espera);
            tiempo.Avanzar(espera);
            return Task.CompletedTask;
        });

        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.Zero);
        tiempo.Avanzar(TimeSpan.FromSeconds(2));
        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.Zero);
        tiempo.Avanzar(TimeSpan.FromSeconds(2));
        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.Zero, "tres llamadas en 4 s caben en el minuto con IA:Rpm=3");
        tiempo.Avanzar(TimeSpan.FromSeconds(6));

        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.FromSeconds(50), "la cuarta espera a que la primera salga de la ventana");
        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.FromSeconds(2));
        tiempo.Avanzar(TimeSpan.FromMinutes(2));
        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.Zero, "pasado el minuto la ventana queda libre");

        limitador.Rpm.Should().Be(3);
        esperas.Should().Equal(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(2));
    }

    // Con el tope de espera agotado no se espera hasta 60 s sin mensaje; sale un 429 local que el canal convierte
    // en el texto del límite (clsTextosAgente.LimiteModelo). Una espera menor que el tope sí se hace.
    [Fact]
    public async Task SiElCupoTardaMasQueElTopeLaLlamadaFallaConUn429SinEsperar()
    {
        var tiempo = new clsTiempoFalso();
        var esperas = new List<TimeSpan>();
        using var limitador = new clsLimitadorRpm(1, tiempo, (espera, _) =>
        {
            esperas.Add(espera);
            tiempo.Avanzar(espera);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20));

        await limitador.EsperarAsync(Ct);
        tiempo.Avanzar(TimeSpan.FromSeconds(30));
        var agotado = async () => await limitador.EsperarAsync(Ct);

        var error = (await agotado.Should().ThrowAsync<HttpRequestException>()).Which;
        error.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        Cotifly.Aplicacion.Agente.Services.clsTextosAgente.ParaFallo(error).Should().Be(Cotifly.Aplicacion.Agente.Services.clsTextosAgente.LimiteModelo);
        esperas.Should().BeEmpty();

        tiempo.Avanzar(TimeSpan.FromSeconds(15));
        (await limitador.EsperarAsync(Ct)).Should().Be(TimeSpan.FromSeconds(15), "15 s caben en el tope de 20 s");
        limitador.EsperaMaxima.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task UnRelojQueNoAvanzaDuranteLaEsperaNoBloqueaElLimitador()
    {
        var tiempo = new clsTiempoFalso();
        using var limitador = new clsLimitadorRpm(1, tiempo, (_, _) => Task.CompletedTask);

        await limitador.EsperarAsync(Ct);
        var espera = await limitador.EsperarAsync(Ct);
        var otra = await limitador.EsperarAsync(Ct);

        espera.Should().Be(TimeSpan.FromMinutes(1));
        otra.Should().Be(TimeSpan.FromMinutes(1), "cada llamada espera su turno aunque el reloj no se mueva, sin quedarse bloqueada");
    }

    [Fact]
    public async Task ElClienteLimitadoRegistraLaEsperaPorElRpm()
    {
        var tiempo = new clsTiempoFalso();
        using var limitador = new clsLimitadorRpm(1, tiempo, (espera, _) =>
        {
            tiempo.Avanzar(espera);
            return Task.CompletedTask;
        });
        var interno = Substitute.For<IChatClient>();
        interno.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var registro = new clsLoggerFalso<clsChatClientLimitado>();
        using var cliente = new clsChatClientLimitado(interno, limitador, registro);

        await cliente.GetResponseAsync([new ChatMessage(ChatRole.User, "hola")], cancellationToken: Ct);
        await cliente.GetResponseAsync([new ChatMessage(ChatRole.User, "hola")], cancellationToken: Ct);

        registro.Registros.Should().ContainSingle().Which.Mensaje.Should().Contain("esperó 60000 ms por IA:Rpm=1");
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
