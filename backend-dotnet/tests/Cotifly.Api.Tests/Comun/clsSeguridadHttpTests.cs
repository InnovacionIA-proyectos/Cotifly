using System.Net;
using Cotifly.Api.Comun;
using Cotifly.Api.Tests.Identidad;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cotifly.Api.Tests.Comun;

/// <summary>
/// Endurecimiento HTTP (#12 parte B): CORS por configuración (preflight), encabezados de seguridad, límite del cuerpo en
/// <c>/web/*</c> y encabezados reenviados de Traefik (la IP real llega al límite de peticiones). Sin base de datos.
/// </summary>
public sealed class clsSeguridadHttpTests
{
    private const string OrigenClientePro = "https://clientepro.boterosoto.com.co";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElPreflightDeUnOrigenPermitidoDevuelveLosEncabezadosCorsSinCredenciales()
    {
        using var factory = Crear(new() { ["Cors:OrigenesPermitidos:0"] = OrigenClientePro });
        using var cliente = factory.CreateClient();

        using var respuesta = await PreflightAsync(cliente, OrigenClientePro, "PUT", "authorization,content-type");

        respuesta.StatusCode.Should().Be(HttpStatusCode.NoContent);
        respuesta.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(OrigenClientePro);
        string.Join(",", respuesta.Headers.GetValues("Access-Control-Allow-Methods")).Should().Contain("PUT");
        string.Join(",", respuesta.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant()
            .Should().Contain("authorization").And.Contain("content-type");
        respuesta.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse("sin cookies ni credenciales");
        respuesta.Headers.GetValues("Access-Control-Max-Age").Should().Equal("600");
    }

    [Theory]
    [InlineData("https://otro-sitio.com", "GET", "authorization")]
    [InlineData(OrigenClientePro, "DELETE", "authorization")]
    [InlineData(OrigenClientePro, "GET", "x-otro-encabezado")]
    public async Task UnPreflightFueraDeLaPoliticaNoRecibePermisoCors(string origen, string metodo, string encabezados)
    {
        using var factory = Crear(new() { ["Cors:OrigenesPermitidos:0"] = OrigenClientePro });
        using var cliente = factory.CreateClient();

        using var respuesta = await PreflightAsync(cliente, origen, metodo, encabezados);

        var permiteTodo = respuesta.Headers.Contains("Access-Control-Allow-Origin")
                          && respuesta.Headers.TryGetValues("Access-Control-Allow-Methods", out var metodos) && string.Join(",", metodos).Contains(metodo, StringComparison.Ordinal)
                          && respuesta.Headers.TryGetValues("Access-Control-Allow-Headers", out var permitidos)
                          && string.Join(",", permitidos).Contains(encabezados, StringComparison.OrdinalIgnoreCase);
        permiteTodo.Should().BeFalse("el navegador bloquea la petición real");
    }

    [Fact]
    public async Task UnaPeticionRealDeUnOrigenPermitidoLlevaElEncabezadoCors()
    {
        using var factory = Crear(new() { ["Cors:OrigenesPermitidos:0"] = OrigenClientePro });
        using var cliente = factory.CreateClient();
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/web/mis-cotizaciones");
        peticion.Headers.Add("Origin", OrigenClientePro);

        using var respuesta = await cliente.SendAsync(peticion, Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        respuesta.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(OrigenClientePro, "el 401 también debe poder leerlo el frontend");
    }

    [Fact]
    public async Task TodasLasRespuestasLlevanLosEncabezadosDeSeguridadYWebNoSeCachea()
    {
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        using var salud = await cliente.GetAsync(new Uri("/health", UriKind.Relative), Ct);
        using var web = await cliente.GetAsync(new Uri("/web/mis-cotizaciones", UriKind.Relative), Ct);

        foreach (var respuesta in new[] { salud, web })
        {
            respuesta.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            respuesta.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
            respuesta.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
            respuesta.Headers.GetValues("Content-Security-Policy").Should().Equal("default-src 'none'; frame-ancestors 'none'");
            respuesta.Headers.Contains("Server").Should().BeFalse();
        }

        web.Headers.CacheControl!.NoStore.Should().BeTrue();
        salud.Headers.CacheControl?.NoStore.Should().NotBe(true);
    }

    [Fact]
    public async Task UnCuerpoDeWebMayorAlLimiteResponde413AntesDeAutenticar()
    {
        using var factory = Crear(new() { ["SeguridadHttp:LimiteCuerpoWebBytes"] = "2048", ["Cors:OrigenesPermitidos:0"] = OrigenClientePro });
        using var cliente = factory.CreateClient();
        using var peticion = new HttpRequestMessage(HttpMethod.Put, "/web/usuario/telefono")
        {
            Content = new StringContent($"{{\"telefono\":\"{new string('1', 3000)}\"}}", System.Text.Encoding.UTF8, "application/json"),
        };
        peticion.Headers.Add("Origin", OrigenClientePro);

        using var grande = await cliente.SendAsync(peticion, Ct);
        using var auth = await cliente.PostAsync(new Uri("/api/auth/logout", UriKind.Relative),
            new StringContent(new string(' ', 3000), System.Text.Encoding.UTF8, "application/json"), Ct);

        grande.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await grande.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"El cuerpo de la petición es demasiado grande."}""");
        grande.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle("el frontend debe poder leer el 413");
        auth.StatusCode.Should().Be(HttpStatusCode.NoContent, "el límite es solo de /web/*");
    }

    [Fact]
    public async Task ConLosEncabezadosReenviadosElLimiteDeAuthVeLaIpRealDelCliente()
    {
        using var factory = Crear(
            new()
            {
                ["ForwardedHeaders:Habilitado"] = "true",
                ["ForwardedHeaders:RedesConocidas:0"] = "10.0.0.0/8",
                ["LimiteAuth:PeticionesPorVentana"] = "1",
                ["LimiteAuth:VentanaSegundos"] = "60",
            },
            conexionDesde: "10.0.9.9");
        using var cliente = factory.CreateClient();

        var primeraIp = await LogoutDesdeAsync(cliente, "203.0.113.10");
        var otraIp = await LogoutDesdeAsync(cliente, "203.0.113.20");
        var repetida = await LogoutDesdeAsync(cliente, "203.0.113.10");
        using var ip = await PedirIpAsync(cliente, "198.51.100.7");

        primeraIp.Should().Be(HttpStatusCode.NoContent);
        otraIp.Should().Be(HttpStatusCode.NoContent, "cada cliente real tiene su propia ventana, no la del proxy");
        repetida.Should().Be(HttpStatusCode.TooManyRequests);
        (await ip.Content.ReadAsStringAsync(Ct)).Should().Be("198.51.100.7");
    }

    [Fact]
    public async Task SinProxyConocidoLosEncabezadosReenviadosSeIgnoran()
    {
        using var factory = Crear(
            new() { ["ForwardedHeaders:Habilitado"] = "true", ["ForwardedHeaders:ProxiesConocidos:0"] = "10.0.0.1" },
            conexionDesde: "192.168.50.50");
        using var cliente = factory.CreateClient();

        using var ip = await PedirIpAsync(cliente, "198.51.100.7");

        (await ip.Content.ReadAsStringAsync(Ct)).Should().Be("192.168.50.50", "un X-Forwarded-For de quien no es el proxy no se cree");
    }

    [Fact]
    public async Task DeshabilitadosLosEncabezadosReenviadosNoCambianLaIp()
    {
        using var factory = Crear(conexionDesde: "10.0.9.9");
        using var cliente = factory.CreateClient();

        using var ip = await PedirIpAsync(cliente, "198.51.100.7");

        (await ip.Content.ReadAsStringAsync(Ct)).Should().Be("10.0.9.9");
    }

    [Theory]
    [InlineData("Production", new string[0], false)]
    [InlineData("Production", new[] { OrigenClientePro }, true)]
    [InlineData("Development", new string[0], true)]
    [InlineData("Development", new[] { "https://a.com/app" }, false)]
    [InlineData("Development", new[] { "https://a.com/" }, false)]
    [InlineData("Development", new[] { "*" }, false)]
    [InlineData("Development", new[] { "https://*.boterosoto.com.co" }, false)]
    [InlineData("Development", new[] { "ftp://a.com" }, false)]
    [InlineData("Development", new[] { "http://localhost:4200" }, true)]
    public void LosOrigenesCorsSeValidanYEnProductionSonObligatorios(string entorno, string[] origenes, bool valido)
    {
        var validador = new clsValidadorOpcionesCors(new clsEntornoHost(entorno));

        var resultado = validador.Validate(null, new clsOpcionesCors { OrigenesPermitidos = [.. origenes] });

        resultado.Succeeded.Should().Be(valido, resultado.FailureMessage);
    }

    [Theory]
    [InlineData(false, new string[0], new string[0], true)]
    [InlineData(true, new string[0], new string[0], false)]
    [InlineData(true, new[] { "10.0.0.1" }, new string[0], true)]
    [InlineData(true, new string[0], new[] { "10.0.0.0/8" }, true)]
    [InlineData(true, new[] { "no-es-ip" }, new string[0], false)]
    [InlineData(true, new string[0], new[] { "10.0.0.0/99" }, false)]
    public void LosEncabezadosReenviadosExigenProxiesORedesValidos(bool habilitado, string[] proxies, string[] redes, bool valido)
    {
        var resultado = new clsValidadorOpcionesEncabezadosReenviados().Validate(null, new clsOpcionesEncabezadosReenviados
        {
            Habilitado = habilitado,
            ProxiesConocidos = [.. proxies],
            RedesConocidas = [.. redes],
        });

        resultado.Succeeded.Should().Be(valido, resultado.FailureMessage);
    }

    private static WebApplicationFactory<Program> Crear(Dictionary<string, string?>? valores = null, string? conexionDesde = null, string entorno = "Pruebas") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(entorno);
            b.ConfigureAppConfiguration((_, configuracion) => configuracion.AddInMemoryCollection(valores ?? []));
            b.ConfigureServices(s =>
            {
                s.AddControllers().AddApplicationPart(typeof(clsProtegidoPruebaController).Assembly);
                if (conexionDesde is not null)
                {
                    s.AddSingleton<IStartupFilter>(new clsIpDeConexion(IPAddress.Parse(conexionDesde)));
                }
            });
        });

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient cliente, string origen, string metodo, string encabezados)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Options, "/web/mis-cotizaciones");
        peticion.Headers.Add("Origin", origen);
        peticion.Headers.Add("Access-Control-Request-Method", metodo);
        peticion.Headers.Add("Access-Control-Request-Headers", encabezados);
        return await cliente.SendAsync(peticion, Ct);
    }

    private static async Task<HttpStatusCode> LogoutDesdeAsync(HttpClient cliente, string ip)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        peticion.Headers.Add("X-Forwarded-For", ip);
        using var respuesta = await cliente.SendAsync(peticion, Ct);
        return respuesta.StatusCode;
    }

    private static async Task<HttpResponseMessage> PedirIpAsync(HttpClient cliente, string ip)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/pruebas/ip");
        peticion.Headers.Add("X-Forwarded-For", ip);
        return await cliente.SendAsync(peticion, Ct);
    }

    /// <summary>TestServer no tiene IP de conexión: se fija la del "proxy" antes de toda la tubería.</summary>
    private sealed class clsIpDeConexion(IPAddress ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((contexto, siguiente) =>
            {
                contexto.Connection.RemoteIpAddress = ip;
                return siguiente(contexto);
            });
            next(app);
        };
    }

    private sealed class clsEntornoHost(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "Cotifly.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
