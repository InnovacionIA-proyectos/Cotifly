using System.Net;
using System.Text;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-013.9 (#105, ADR-018 §14): Google Chat y el agente cotizador legado se retiraron. El webhook <c>POST /chat/events</c> ya no
/// existe (404), la API arranca sin las variables <c>GChat__*</c> y las ignora si un despliegue viejo todavía las trae (también el
/// flag <c>Chat:CreacionHabilitada</c>), y las cotizaciones creadas por el chat antes del retiro (<c>estadoCosteo = null</c>) siguen
/// en Mis cotizaciones, admiten el seguimiento y cuentan en las métricas.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsRetiroGoogleChatTests(clsEntornoApiWeb entorno)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Configuración que traía un despliegue con Google Chat. Con las opciones de antes, la cuenta ilegible tumbaba el arranque.</summary>
    private static readonly Dictionary<string, string?> _variablesHeredadas = new()
    {
        ["GChat:ProjectNumber"] = "no-es-numero",
        ["GChat:VerificarJwt"] = "false",
        ["GChat:SaJson"] = "/run/secrets/no-existe-sa.json",
        ["GChat:SupportSpace"] = "soporte",
        ["Chat:CreacionHabilitada"] = "true",
    };

    [Theory]
    [InlineData("POST", true)]
    [InlineData("POST", false)]
    [InlineData("GET", false)]
    public async Task ElWebhookDeGoogleChatYaNoExisteYResponde404(string metodo, bool conBearer)
    {
        using var cliente = entorno.Api.Factory.CreateClient();
        using var peticion = new HttpRequestMessage(new HttpMethod(metodo), "/chat/events");
        if (conBearer)
        {
            peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "eyJhbGciOiJSUzI1NiJ9.e30.firma");
        }

        if (metodo == "POST")
        {
            peticion.Content = new StringContent("""{"type":"MESSAGE","message":{"text":"Cotiza"}}""", Encoding.UTF8, "application/json");
        }

        using var respuesta = await cliente.SendAsync(peticion, Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConLasVariablesGChatHeredadasLaApiArrancaYLasIgnora()
    {
        using var api = new clsApiWebPruebas(entorno.BaseDatos, extra: _variablesHeredadas);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var salud = await api.EnviarAsync(HttpMethod.Get, "/health", null);
        using var webhook = await api.EnviarAsync(HttpMethod.Post, "/chat/events", null, "{}");
        using var sinFolio = await api.EnviarAsync(HttpMethod.Post, "/web/chat", ana.Access, clsApiWebPruebas.Json(new { mensaje = "Cotiza", sesion_id = Guid.NewGuid().ToString("N") }));

        salud.StatusCode.Should().Be(HttpStatusCode.OK);
        webhook.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await VerificarContratoAsync(sinFolio, "/web/chat", HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("FOLIO_REQUERIDO", "Chat:CreacionHabilitada ya no reactiva el agente legado");
    }

    // Igual en Production, donde antes GChat:VerificarJwt=false o una cuenta de servicio ilegible tumbaban el arranque.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnProductionArrancaSinYConLasVariablesGChatYNoHayWebhookNiChatSinFolio(bool conHeredadas)
    {
        var valores = new Dictionary<string, string?>(conHeredadas ? _variablesHeredadas : [])
        {
            ["Cors:OrigenesPermitidos:0"] = "https://clientepro.boterosoto.com.co",
        };
        using var api = new clsApiWebPruebas(entorno.BaseDatos, extra: valores, entorno: "Production");
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var salud = await api.EnviarAsync(HttpMethod.Get, "/health", null);
        using var webhook = await api.EnviarAsync(HttpMethod.Post, "/chat/events", null, "{}");
        using var sinFolio = await api.EnviarAsync(HttpMethod.Post, "/web/chat", ana.Access, clsApiWebPruebas.Json(new { mensaje = "Cotiza", sesion_id = Guid.NewGuid().ToString("N") }));

        salud.StatusCode.Should().Be(HttpStatusCode.OK);
        (await salud.Content.ReadAsStringAsync(Ct)).Should().Contain("Production");
        webhook.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await VerificarContratoAsync(sinFolio, "/web/chat", HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("FOLIO_REQUERIDO");
    }

    [Fact]
    public void NoQuedanOpcionesNiServiciosDeGoogleChat()
    {
        var servicios = entorno.Api.Factory.Services;
        var tipos = new[] { typeof(Program).Assembly, typeof(Cotifly.Aplicacion.clsDependenciasAplicacion).Assembly, typeof(Cotifly.Infraestructura.clsDependenciasInfraestructura).Assembly, typeof(Cotifly.Dominio.Comun.clsCodigosError).Assembly }
            .SelectMany(a => a.GetTypes())
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        tipos.Should().NotContain(t => t.Contains("GoogleChat", StringComparison.OrdinalIgnoreCase) || t.Contains("GChat", StringComparison.Ordinal),
            "Google Chat se retiró por completo (#105)");
        tipos.Should().NotContain(t => t.EndsWith(".clsOpcionesChat", StringComparison.Ordinal) || t.EndsWith(".clsAvisoArranqueChat", StringComparison.Ordinal));
        servicios.GetServices<IHostedService>().Select(h => h.GetType().Name)
            .Should().NotContain(n => n.Contains("Chat", StringComparison.OrdinalIgnoreCase), "sin worker ni aviso de arranque del chat");
    }

    // Las cotizaciones del chat legado (estadoCosteo = null, costo manual) son datos: siguen visibles, se marcan con /seguimiento y
    // cuentan en las métricas igual que antes del retiro.
    [Fact]
    public async Task LasCotizacionesDeChatAnterioresSiguenEnMisCotizacionesElSeguimientoYLasMetricas()
    {
        var api = entorno.Api;
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var deChat = await api.CrearCotizacionAsync(ana.UsuarioId, "900777888", conPdf: false);
        (await clsBaseDatosPruebas.EscalarAsync<string>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT estadoCosteo FROM dbo_cotifly.Cotizaciones WHERE id = '{deChat.Id}'")).Should().BeNull("así quedaron las del chat");

        using var mis = await api.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", ana.Access);
        var lista = await VerificarContratoAsync(mis, "/web/mis-cotizaciones", HttpMethod.Get, HttpStatusCode.OK);
        using var seguimiento = await api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{deChat.Folio}/seguimiento", ana.Access, clsApiWebPruebas.Json(new { ganada = true }));
        var hoy = DateTime.UtcNow;
        using var metricas = await api.EnviarAsync(HttpMethod.Get, $"/web/metricas/comercial?desde={hoy.Year}-01-01&hasta={hoy.Year}-12-31", ana.Access);
        var indicadores = (await VerificarContratoAsync(metricas, "/web/metricas/comercial", HttpMethod.Get, HttpStatusCode.OK)).GetProperty("indicadores");

        lista.GetProperty("cotizaciones").EnumerateArray().Select(c => c.GetProperty("folio").GetString()).Should().Equal(deChat.Folio);
        (await VerificarContratoAsync(seguimiento, "/web/cotizaciones/{folio}/seguimiento", HttpMethod.Post, HttpStatusCode.OK))
            .GetProperty("ganada").GetBoolean().Should().BeTrue();
        indicadores.GetProperty("cantidad").GetInt32().Should().Be(1);
        indicadores.GetProperty("valor_ganado_cop").GetDecimal().Should().Be(1_176_471m);
        indicadores.GetProperty("ganancias").GetDecimal().Should().Be(176_471m);
        indicadores.GetProperty("cotizaciones_usd").GetInt32().Should().Be(0);
        lista.GetProperty("total").ValueKind.Should().Be(JsonValueKind.Number);
    }
}
