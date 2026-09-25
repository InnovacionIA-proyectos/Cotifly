using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>POST /chat/events</c> (port de <c>tests/test_chat_webhook.py</c>): JWT de Google verificado con los certificados falsos de la
/// fixture, acuse inmediato en el formato de cada modelo de app y encolado para el worker. Sin base de datos ni red.
/// </summary>
public sealed class clsChatEventosEndpointTests : IDisposable
{
    private const string Ruta = "/chat/events";
    private static readonly string _eventoReal = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "chat", "evento_mensaje.json"));
    private readonly clsGoogleFalso _google = new();
    private readonly clsColaFalsa _cola = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _google.Dispose();

    private WebApplicationFactory<Program> Fabrica(bool verificar = true)
    {
        var configuracion = _google.Configuracion(conCuentaServicio: false);
        configuracion["GChat:VerificarJwt"] = verificar ? "true" : "false";
        return new clsCotiflyApiFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(configuracion));
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient(clsVerificadorJwtGoogleChat.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => _google);
                s.AddSingleton<IClsColaEventosChat>(_cola);
            });
        });
    }

    private static async Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, string cuerpo, string? token)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, Ruta) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await cliente.SendAsync(peticion, Ct);
    }

    // test_jwt_valido_ack_200 + test_ack_no_espera_procesamiento
    [Fact]
    public async Task ConElJwtValidoAcusaDeInmediatoYEncolaElMensaje()
    {
        using var fabrica = Fabrica();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await EnviarAsync(cliente, _eventoReal, clsGoogleFalso.Fixture("jwt_valido.txt"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("text").GetString().Should().Be("Recibí tu solicitud, la estoy procesando…");
        var evento = _cola.Eventos.Should().ContainSingle().Subject;
        evento.SpaceId.Should().Be("spaces/AAAAbot_cotizador");
        evento.ThreadId.Should().Be("spaces/AAAAbot_cotizador/threads/tLongyin19032026");
        evento.UsuarioEmail.Should().Be("nvargas@boterosoto.com.co");
    }

    // test_jwt_ausente_401 / test_jwt_invalido_401 / test_bearer_malformado_401
    [Theory]
    [InlineData(null)]
    [InlineData("jwt_invalido.txt")]
    [InlineData("otro-token-cualquiera")]
    public async Task SinJwtOConUnoInvalidoEs401YNoEncola(string? token)
    {
        using var fabrica = Fabrica();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await EnviarAsync(cliente, _eventoReal, token is "jwt_invalido.txt" ? clsGoogleFalso.Fixture(token) : token);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"JWT inválido o ausente"}""");
        _cola.Eventos.Should().BeEmpty();
    }

    // test_evento_no_mensaje_no_encola
    [Fact]
    public async Task UnEventoQueNoEsMensajeAcusaSinCuerpoYNoEncola()
    {
        using var fabrica = Fabrica();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await EnviarAsync(cliente, """{"type":"ADDED_TO_SPACE","space":{"name":"spaces/X"}}""", clsGoogleFalso.Fixture("jwt_valido.txt"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("{}");
        _cola.Eventos.Should().BeEmpty();
    }

    // test_webhook_formato_addon_ack_hostappdataaction + test_webhook_sin_jwt_cuando_verificacion_desactivada
    [Fact]
    public async Task ElFormatoAddOnAcusaConHostAppDataActionYSinVerificacionNoPideJwt()
    {
        using var fabrica = Fabrica(verificar: false);
        using var cliente = fabrica.CreateClient();
        var cuerpo = """
            {"chat":{"messagePayload":{"message":{"text":"hola","thread":{"name":"spaces/S/threads/T"},
             "sender":{"email":"d@x.co","displayName":"D"}},"space":{"name":"spaces/S"}}}}
            """;

        using var respuesta = await EnviarAsync(cliente, cuerpo, null);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("hostAppDataAction").GetProperty("chatDataAction").GetProperty("createMessageAction")
            .GetProperty("message").GetProperty("text").GetString().Should().NotBeNullOrWhiteSpace();
        _cola.Eventos.Should().ContainSingle().Which.Texto.Should().Be("hola");
    }

    [Fact]
    public async Task UnCuerpoQueNoEsJsonEs400()
    {
        using var fabrica = Fabrica();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await EnviarAsync(cliente, "esto no es json", clsGoogleFalso.Fixture("jwt_valido.txt"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"payload no es JSON"}""");
    }

    [Fact]
    public async Task ConLaColaLlenaAcusaPeroAvisa()
    {
        _cola.Llena = true;
        using var fabrica = Fabrica();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await EnviarAsync(cliente, _eventoReal, clsGoogleFalso.Fixture("jwt_valido.txt"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Contain("muchas solicitudes");
    }

    /// <summary>Cola que registra lo encolado; el worker del host espera sin consumir.</summary>
    private sealed class clsColaFalsa : IClsColaEventosChat
    {
        public ConcurrentQueue<clsEventoChat> Eventos { get; } = new();

        public bool Llena { get; set; }

        public bool Encolar(clsEventoChat evento)
        {
            if (Llena)
            {
                return false;
            }

            Eventos.Enqueue(evento);
            return true;
        }

        public async ValueTask<clsEventoChat> LeerAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
