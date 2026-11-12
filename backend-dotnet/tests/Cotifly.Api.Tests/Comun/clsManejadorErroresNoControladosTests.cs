using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cotifly.Api.Comun;
using Cotifly.Pruebas.Contrato;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotifly.Api.Tests.Comun;

/// <summary>
/// El manejador global de errores no controlados. Un 500 responde <c>ErrorNegocio</c> <c>ERROR_INTERNO</c> con
/// el mensaje genérico (<c>ErrorWeb</c> en <c>/api/*</c>), sin el mensaje de la excepción ni la traza, con los encabezados de seguridad, y
/// deja la excepción en el log con el id de correlación. Una petición abortada por el cliente no se responde ni se registra como error.
/// </summary>
public sealed class clsManejadorErroresNoControladosTests
{
    private const string Secreto = "Server=tbsdatacenter;Password=NoDebeSalir123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnaExcepcionNoControladaEnWebResponde500ErrorNegocioGenericoSinDetallesTecnicos()
    {
        var registro = new clsRegistroLogs();
        using var factory = Crear(registro);
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.GetAsync(new Uri("/pruebas-error/falla", UriKind.Relative), Ct);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        cuerpo.Should().NotContain("Password").And.NotContain(nameof(InvalidOperationException)).And.NotContain("at Cotifly");
        using var json = JsonDocument.Parse(cuerpo);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("ERROR_INTERNO");
        error.GetProperty("mensaje").GetString().Should().Be(clsManejadorErroresNoControlados.MensajeErrorInterno);
        var detalle = error.GetProperty("detalle");
        detalle.EnumerateObject().Select(p => p.Name).Should().Equal(["id_correlacion"], "sin detalles técnicos: solo el id de correlación");
        var idCorrelacion = detalle.GetProperty("id_correlacion").GetString();
        idCorrelacion.Should().NotBeNullOrWhiteSpace();
        clsValidadorEsquemaOpenApi.Validar(cuerpo, await clsValidadorEsquemaOpenApi.EsquemaAsync("ErrorNegocio")).Should().BeEmpty();
        respuesta.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        respuesta.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");

        var entrada = registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error && e.Excepcion is InvalidOperationException).Subject;
        entrada.Mensaje.Should().Contain("GET /pruebas-error/falla").And.Contain($"id de correlación {idCorrelacion}", "el id de la respuesta lleva al log");
        entrada.Excepcion!.Message.Should().Contain(Secreto, "el detalle técnico queda en el log");
        registro.Entradas.Should().NotContain(e => e.Categoria.StartsWith("Microsoft.AspNetCore.Diagnostics", StringComparison.Ordinal) && e.Nivel >= LogLevel.Error,
            "la excepción se registra una sola vez, con el id de correlación");
    }

    /// <summary>
    /// El manejador va antes de CORS en la tubería y el middleware de excepciones limpia los
    /// encabezados; el 500 de un origen permitido igual debe llevar <c>Access-Control-Allow-Origin</c>, o el navegador lo convierte en un
    /// error de red opaco y la consola no puede leer el <c>id_correlacion</c>. Uno de otro origen no lo lleva.
    /// </summary>
    [Fact]
    public async Task El500DeUnOrigenPermitidoLlevaLosEncabezadosCors()
    {
        const string Origen = "https://clientepro.boterosoto.com.co";
        var registro = new clsRegistroLogs();
        using var factory = Crear(registro, new() { ["Cors:OrigenesPermitidos:0"] = Origen });
        using var cliente = factory.CreateClient();

        using var permitida = new HttpRequestMessage(HttpMethod.Get, new Uri("/pruebas-error/falla", UriKind.Relative));
        permitida.Headers.Add("Origin", Origen);
        using var respuesta = await cliente.SendAsync(permitida, Ct);
        using var ajena = new HttpRequestMessage(HttpMethod.Get, new Uri("/pruebas-error/falla", UriKind.Relative));
        ajena.Headers.Add("Origin", "https://otro-sitio.com");
        using var respuestaAjena = await cliente.SendAsync(ajena, Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        respuesta.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(Origen);
        respuesta.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        respuestaAjena.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        respuestaAjena.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task EnApiResponde500ErrorWebGenerico()
    {
        using var factory = Crear(new clsRegistroLogs());
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.GetAsync(new Uri("/api/pruebas-error/falla", UriKind.Relative), Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        JsonSerializer.Deserialize<clsErrorWeb>(await respuesta.Content.ReadAsStringAsync(Ct))
            .Should().Be(new clsErrorWeb(clsManejadorErroresNoControlados.MensajeErrorInterno), "en /api/* el error es ErrorWeb");
    }

    [Fact]
    public async Task ElCuerpoQuePasaElLimiteSinContentLengthSigueSiendo413()
    {
        using var factory = Crear(new clsRegistroLogs());
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.GetAsync(new Uri("/pruebas-error/cuerpo-grande", UriKind.Relative), Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Contain(clsSeguridadHttp.MensajeCuerpoDemasiadoGrande);
    }

    [Fact]
    public async Task UnaPeticionAbortadaPorElClienteNoSeRespondeNiSeRegistraComoError()
    {
        var registro = new clsRegistroLogs();
        using var abortada = new CancellationTokenSource();
        await abortada.CancelAsync();
        var contexto = Contexto(registro, new OperationCanceledException(abortada.Token));
        contexto.RequestAborted = abortada.Token;

        await clsManejadorErroresNoControlados.ManejarAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
        contexto.Response.Body.Length.Should().Be(0);
        registro.Entradas.Should().NotContain(e => e.Nivel >= LogLevel.Warning);
    }

    [Fact]
    public async Task UnaCancelacionQueNoVieneDelClienteSiEsUnErrorInterno()
    {
        var registro = new clsRegistroLogs();
        var contexto = Contexto(registro, new TaskCanceledException("tiempo agotado contra una dependencia"));

        await clsManejadorErroresNoControlados.ManejarAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error && e.Mensaje.Contains(contexto.TraceIdentifier, StringComparison.Ordinal));
    }

    private static DefaultHttpContext Contexto(clsRegistroLogs registro, Exception excepcion)
    {
        var contexto = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging(l => l.AddProvider(registro)).BuildServiceProvider(),
            TraceIdentifier = "0HN-CORRELACION:00000001",
        };
        contexto.Request.Method = "POST";
        contexto.Request.Path = "/web/cotizaciones/PCN-X/enviar";
        contexto.Response.Body = new MemoryStream();
        contexto.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = excepcion, Path = "/web/cotizaciones/PCN-X/enviar" });
        return contexto;
    }

    private static WebApplicationFactory<Program> Crear(clsRegistroLogs registro, Dictionary<string, string?>? valores = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(clsCotiflyApiFactory.EntornoPruebas);
            b.ConfigureAppConfiguration((_, configuracion) => configuracion.AddInMemoryCollection(valores ?? []));
            b.ConfigureLogging(l => l.AddProvider(registro));
            b.ConfigureServices(s => s.AddControllers().AddApplicationPart(typeof(clsErrorPruebaController).Assembly));
        });

    /// <summary>Registro en memoria de los logs del host de pruebas.</summary>
    private sealed class clsRegistroLogs : ILoggerProvider
    {
        public ConcurrentQueue<(string Categoria, LogLevel Nivel, string Mensaje, Exception? Excepcion)> Entradas { get; } = new();

        public ILogger CreateLogger(string categoryName) => new clsLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class clsLogger(clsRegistroLogs registro, string categoria) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                registro.Entradas.Enqueue((categoria, logLevel, formatter(state, exception), exception));
        }
    }
}

/// <summary>Endpoints SOLO del host de pruebas del manejador de errores (se agregan con <c>AddApplicationPart</c>).</summary>
[ApiController]
[AllowAnonymous]
public sealed class clsErrorPruebaController : ControllerBase
{
    [HttpGet("pruebas-error/falla")]
    [HttpGet("api/pruebas-error/falla")]
    public IActionResult Falla() => throw new InvalidOperationException("Login failed: Server=tbsdatacenter;Password=NoDebeSalir123");

    [HttpGet("pruebas-error/cuerpo-grande")]
    public IActionResult CuerpoGrande() => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
}
