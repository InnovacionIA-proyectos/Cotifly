using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>POST /web/chat</c> por HTTP contra la base real con el modelo guionizado (port de <c>web_chat</c> de
/// <c>orquestador/web.py</c>): 200 <c>RespuestaChat</c> con precio y margen de las herramientas, 400 por <c>sesion_id</c>, 404 si la
/// sesión es de otro comercial, 200 <c>error: true</c> ante un fallo del proveedor (texto del límite de peticiones), 429 propio y
/// la descarga del PDF con el enlace firmado de Google Chat (<c>?t=</c>).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsChatWebEndpointTests(clsEntornoApiWeb entorno)
{
    private const string RutaChat = "/web/chat";
    private static readonly HttpMethod _post = HttpMethod.Post;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SesionId() => Guid.NewGuid().ToString("N");

    private clsApiWebPruebas Api(clsChatClientGuionizado modelo, IDictionary<string, string?>? extra = null) =>
        new(entorno.BaseDatos, extra: extra, configurar: b => b.ConfigureTestServices(s => s.AddSingleton<IChatClient>(modelo)));

    private static string Mensaje(string texto, string sesion) => clsApiWebPruebas.Json(new { mensaje = texto, sesion_id = sesion });

    [Fact]
    public async Task UnTurnoDevuelveElTextoLasHerramientasYElPrecioYMargenDeLasHerramientas()
    {
        var modelo = new clsChatClientGuionizado()
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.9, metodo = "markup" })
            .Cerrar("Previsualización lista. ¿Confirmas?");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Cotiza Nacional YUMBO a BUN, costo 2.850.000", SesionId()));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("texto").GetString().Should().Be("Previsualización lista. ¿Confirmas?");
        json.GetProperty("herramientas").EnumerateArray().Select(h => h.GetString()).Should().Equal("obtener_margen", "calcular_precio");
        json.GetProperty("margen").GetProperty("origen").GetString().Should().Be("politica_linea");
        var precio = json.GetProperty("precio");
        precio.GetProperty("margen").GetDecimal().Should().Be(json.GetProperty("margen").GetProperty("margen").GetDecimal(), "el margen sale de obtener_margen, no del LLM");
        precio.GetProperty("metodo").GetString().Should().Be("margen");
        json.GetProperty("pdf").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("corto")]
    [InlineData("con espacios 123")]
    [InlineData("../../etc/passwd")]
    public async Task UnSesionIdInvalidoEs400(string sesion)
    {
        using var api = Api(new clsChatClientGuionizado());
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", sesion));

        (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.BadRequest)).GetProperty("error").GetString().Should().Be("sesion_id inválido");
    }

    [Fact]
    public async Task UnMensajeVacioODemasiadoLargoEs400()
    {
        using var api = Api(new clsChatClientGuionizado());
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var vacio = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje(" ", SesionId()));
        using var largo = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje(new string('a', 8001), SesionId()));

        await VerificarContratoAsync(vacio, RutaChat, _post, HttpStatusCode.BadRequest);
        await VerificarContratoAsync(largo, RutaChat, _post, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SinSesionEs401()
    {
        using var api = Api(new clsChatClientGuionizado());

        using var respuesta = await api.EnviarAsync(_post, RutaChat, null, Mensaje("hola", SesionId()));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Inicia sesión de nuevo."}""");
    }

    // ADR-004 §6: la sesión queda ligada a su usuario; otro comercial recibe 404 y la conversación no cambia.
    [Fact]
    public async Task LaSesionDeOtroComercialEs404()
    {
        var modelo = new clsChatClientGuionizado().Cerrar("Hola Ana.");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var sesion = SesionId();

        using var deAna = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", sesion));
        using var deLuis = await api.EnviarAsync(_post, RutaChat, luis.Access, Mensaje("¿qué cotizó Ana?", sesion));

        deAna.StatusCode.Should().Be(HttpStatusCode.OK);
        (await VerificarContratoAsync(deLuis, RutaChat, _post, HttpStatusCode.NotFound)).GetProperty("error").GetString().Should().Be("Sesión no encontrada.");
        modelo.Llamadas.Should().HaveCount(2, "el turno de Luis no llega al modelo");
        (await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosConversacion WHERE spaceId = 'web/{sesion}' AND rol = 'user'")).Should().Be(1);
    }

    // Paridad con el Python: un fallo del proveedor responde 200 con error = true y el texto del límite de peticiones.
    [Fact]
    public async Task UnFalloDelProveedorEs200ConErrorYElTextoDelLimite()
    {
        var modelo = new clsChatClientGuionizado()
            .Fallar(new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests))
            .Fallar(new InvalidOperationException("se cayó"));
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var limite = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId()));
        using var interno = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId()));

        var jsonLimite = await VerificarContratoAsync(limite, RutaChat, _post, HttpStatusCode.OK);
        jsonLimite.GetProperty("error").GetBoolean().Should().BeTrue();
        jsonLimite.GetProperty("texto").GetString().Should().StartWith("⏳ El modelo alcanzó su límite de peticiones por minuto.");
        (await VerificarContratoAsync(interno, RutaChat, _post, HttpStatusCode.OK)).GetProperty("texto").GetString()
            .Should().Be("Ocurrió un error interno procesando tu solicitud. Inténtalo de nuevo.");
    }

    [Fact]
    public async Task SinProveedorConfiguradoElTurnoEsUnErrorControlado()
    {
        using var api = new clsApiWebPruebas(entorno.BaseDatos);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId()));

        (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK)).GetProperty("error").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ElLimiteDeLaApiResponde429ConErrorWeb()
    {
        var modelo = new clsChatClientGuionizado().Cerrar("uno");
        using var api = Api(modelo, new Dictionary<string, string?> { ["LimiteChat:PeticionesPorVentana"] = "1", ["LimiteChat:VentanaSegundos"] = "3600" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var primera = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId()));
        using var segunda = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId()));

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        await VerificarContratoAsync(segunda, RutaChat, _post, HttpStatusCode.TooManyRequests);
    }

    // ADR-011 §3: el enlace firmado abre el PDF sin Authorization; alterado o de otro comercial, no.
    [Fact]
    public async Task ElEnlaceFirmadoAbreElPdfSinBearerYAlteradoNo()
    {
        var api = entorno.Api;
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var firma = api.Factory.Services.GetRequiredService<IClsFirmaUrlPdf>();
        var token = firma.Firmar(cotizacion.ArchivoPdf!, ana.UsuarioId);
        var deLuis = firma.Firmar(cotizacion.ArchivoPdf!, luis.UsuarioId);

        using var valido = await api.EnviarAsync(HttpMethod.Get, $"/pdfs/{cotizacion.ArchivoPdf}?t={Uri.EscapeDataString(token)}", null);
        using var alterado = await api.EnviarAsync(HttpMethod.Get, $"/pdfs/{cotizacion.ArchivoPdf}?t={Uri.EscapeDataString(token[..^3] + "abc")}", null);
        using var ajeno = await api.EnviarAsync(HttpMethod.Get, $"/pdfs/{cotizacion.ArchivoPdf}?t={Uri.EscapeDataString(deLuis)}", null);

        valido.StatusCode.Should().Be(HttpStatusCode.OK);
        valido.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await valido.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(clsApiWebPruebas.PdfMinimo);
        alterado.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await alterado.Content.ReadAsStringAsync(Ct)).Should().Contain("venció o no es válido");
        ajeno.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
