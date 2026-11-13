using System.Net;
using Cotifly.Api.Sistema;

namespace Cotifly.Api.Tests.Sistema;

/// <summary>
/// Sonda del HEALTHCHECK del Dockerfile: <c>dotnet Cotifly.Api.dll --healthcheck</c>
/// hace GET a <c>/health</c> del propio contenedor y termina con 0 (sano) o 1 (no sano),
/// sin levantar el host web.
/// </summary>
public sealed class clsSondaSaludTests
{
    [Theory]
    [InlineData(new[] { "--healthcheck" }, true)]
    [InlineData(new[] { "--urls", "http://+:8080", "--healthcheck" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--HEALTHCHECK" }, false)]
    [InlineData(new[] { "healthcheck" }, false)]
    public void DetectaSiSeSolicitaLaSonda(string[] args, bool esperado)
    {
        clsSondaSalud.EsSolicitada(args).Should().Be(esperado);
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health")]
    [InlineData("", "http://127.0.0.1:8080/health")]
    [InlineData("   ", "http://127.0.0.1:8080/health")]
    [InlineData("8080", "http://127.0.0.1:8080/health")]
    [InlineData("5000", "http://127.0.0.1:5000/health")]
    [InlineData("5000;6000", "http://127.0.0.1:5000/health")]
    [InlineData(" 7000 ; 8000", "http://127.0.0.1:7000/health")]
    [InlineData("abc;6000", "http://127.0.0.1:6000/health")]
    [InlineData("abc", "http://127.0.0.1:8080/health")]
    [InlineData("0", "http://127.0.0.1:8080/health")]
    [InlineData("70000", "http://127.0.0.1:8080/health")]
    public void SinAspNetCoreUrlsUsaElPrimerPuertoDeAspNetCoreHttpPorts(string? puertosHttp, string esperada)
    {
        clsSondaSalud.ConstruirUrl(null, puertosHttp).Should().Be(new Uri(esperada));
    }

    [Theory]
    [InlineData("http://+:5000", "http://127.0.0.1:5000/health")]
    [InlineData("http://*:5001", "http://127.0.0.1:5001/health")]
    [InlineData("http://0.0.0.0:5002", "http://127.0.0.1:5002/health")]
    [InlineData("http://[::]:5003", "http://127.0.0.1:5003/health")]
    [InlineData("http://localhost:5004", "http://localhost:5004/health")]
    [InlineData("http://10.0.0.5:5005", "http://10.0.0.5:5005/health")]
    [InlineData("http://[::1]:5006", "http://[::1]:5006/health")]
    [InlineData("HTTP://+:5007", "http://127.0.0.1:5007/health")]
    [InlineData("http://+:5008/base", "http://127.0.0.1:5008/health")]
    [InlineData("http://+", "http://127.0.0.1:80/health")]
    [InlineData("http://[::]", "http://127.0.0.1:80/health")]
    [InlineData("https://+:8443;http://+:5009", "http://127.0.0.1:5009/health")]
    [InlineData("http://+:5010;http://+:5011", "http://127.0.0.1:5010/health")]
    [InlineData(" http://+:5012 , https://+:8443", "http://127.0.0.1:5012/health")]
    public void AspNetCoreUrlsMandaSobreLosPuertosYUsaLaPrimeraUrlHttp(string urls, string esperada)
    {
        clsSondaSalud.ConstruirUrl(urls, "7000").Should().Be(new Uri(esperada));
    }

    [Theory]
    [InlineData("https://+:8443", "7000", "http://127.0.0.1:7000/health")]
    [InlineData("https://+:8443", null, "http://127.0.0.1:8080/health")]
    [InlineData("http://+:abc", "7000", "http://127.0.0.1:7000/health")]
    [InlineData("http://+:0", null, "http://127.0.0.1:8080/health")]
    [InlineData("http://:5000", "7000", "http://127.0.0.1:7000/health")]
    [InlineData("   ", "7000", "http://127.0.0.1:7000/health")]
    public void SinUrlHttpValidaEnAspNetCoreUrlsCaeAlPuertoYLuegoAl8080(string urls, string? puertosHttp, string esperada)
    {
        clsSondaSalud.ConstruirUrl(urls, puertosHttp).Should().Be(new Uri(esperada));
    }

    [Fact]
    public void ElManejadorNoUsaProxy()
    {
        using var manejador = clsSondaSalud.CrearManejador();

        manejador.UseProxy.Should().BeFalse();
    }

    [Fact]
    public async Task Con200TerminaEnCero()
    {
        using var cliente = ClienteQueResponde(HttpStatusCode.OK);
        using var salida = new StringWriter();

        var codigo = await clsSondaSalud.EjecutarAsync(cliente, _urlPrueba, salida, TestContext.Current.CancellationToken);

        codigo.Should().Be(clsSondaSalud.CodigoSano);
        clsSondaSalud.CodigoSano.Should().Be(0);
        salida.ToString().Should().Contain("200");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task ConOtroEstadoTerminaEnUno(HttpStatusCode estado)
    {
        using var cliente = ClienteQueResponde(estado);
        using var salida = new StringWriter();

        var codigo = await clsSondaSalud.EjecutarAsync(cliente, _urlPrueba, salida, TestContext.Current.CancellationToken);

        codigo.Should().Be(clsSondaSalud.CodigoNoSano);
        clsSondaSalud.CodigoNoSano.Should().Be(1);
        salida.ToString().Should().Contain(((int)estado).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SiLaApiNoRespondeTerminaEnUno()
    {
        using var cliente = new HttpClient(new clsManejadorFalso(_ => throw new HttpRequestException("Connection refused")));
        using var salida = new StringWriter();

        var codigo = await clsSondaSalud.EjecutarAsync(cliente, _urlPrueba, salida, TestContext.Current.CancellationToken);

        codigo.Should().Be(clsSondaSalud.CodigoNoSano);
        salida.ToString().Should().Contain("Connection refused");
    }

    [Fact]
    public async Task SiSeAgotaElTiempoTerminaEnUno()
    {
        using var cliente = new HttpClient(new clsManejadorFalso(_ => throw new TaskCanceledException("timeout")));
        using var salida = new StringWriter();

        var codigo = await clsSondaSalud.EjecutarAsync(cliente, _urlPrueba, salida, TestContext.Current.CancellationToken);

        codigo.Should().Be(clsSondaSalud.CodigoNoSano);
        salida.ToString().Should().Contain("tiempo");
    }

    [Fact]
    public void ElTiempoLimiteCabeEnElTimeoutDelHealthcheckDelDockerfile()
    {
        // HEALTHCHECK --timeout=5s: la sonda debe rendirse antes para responder 1 y no quedar colgada.
        clsSondaSalud.TiempoLimite.Should().BeLessThan(TimeSpan.FromSeconds(5));

        using var cliente = clsSondaSalud.CrearCliente();
        cliente.Timeout.Should().Be(clsSondaSalud.TiempoLimite);
    }

    [Fact]
    public async Task ContraLaApiRealLaSondaDaSano()
    {
        using var factory = new clsCotiflyApiFactory();
        using var cliente = factory.CreateClient();
        using var salida = new StringWriter();

        var codigo = await clsSondaSalud.EjecutarAsync(
            cliente, new Uri(cliente.BaseAddress!, "/health"), salida, TestContext.Current.CancellationToken);

        codigo.Should().Be(clsSondaSalud.CodigoSano);
    }

    private static readonly Uri _urlPrueba = new("http://127.0.0.1:8080/health");

    private static HttpClient ClienteQueResponde(HttpStatusCode estado) =>
        new(new clsManejadorFalso(_ => new HttpResponseMessage(estado)));

    private sealed class clsManejadorFalso(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
