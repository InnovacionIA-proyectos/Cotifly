using System.Net;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Reglas del canal <c>POST /web/chat</c> por HTTP contra la base real con el modelo guionizado, comunes al asistente por folio
/// (HU-013.6; desde HU-013.9 el agente cotizador legado ya no existe): 400 por <c>sesion_id</c> o mensaje, 401 sin sesión, validación
/// de la respuesta final con reintento y respaldo (HU-003.10), 200 <c>error: true</c> ante un fallo del proveedor, 429 propio y la
/// descarga del PDF con el enlace firmado (<c>?t=</c>, ADR-011 §3).
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

    private static string Mensaje(string texto, string sesion, string? folio = null) =>
        clsApiWebPruebas.Json(new { mensaje = texto, sesion_id = sesion, folio });

    [Theory]
    [InlineData("corto")]
    [InlineData("con espacios 123")]
    [InlineData("../../etc/passwd")]
    public async Task UnSesionIdInvalidoEs400(string sesion)
    {
        using var api = Api(new clsChatClientGuionizado());
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", sesion, "PCN-NV-24092026-001"));

        (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.BadRequest)).GetProperty("error").GetString().Should().Be("sesion_id inválido");
    }

    [Fact]
    public async Task UnMensajeVacioODemasiadoLargoEs400()
    {
        using var api = Api(new clsChatClientGuionizado());
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var vacio = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje(" ", SesionId(), "PCN-NV-24092026-001"));
        using var largo = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje(new string('a', 8001), SesionId(), "PCN-NV-24092026-001"));

        await VerificarContratoAsync(vacio, RutaChat, _post, HttpStatusCode.BadRequest);
        await VerificarContratoAsync(largo, RutaChat, _post, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SinSesionEs401()
    {
        using var api = Api(new clsChatClientGuionizado());

        using var respuesta = await api.EnviarAsync(_post, RutaChat, null, Mensaje("hola", SesionId(), "PCN-NV-24092026-001"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Inicia sesión de nuevo."}""");
    }

    // HU-003.10 (#16): una respuesta final malformada se reintenta una vez con la corrección y llega la válida; con el JSON Schema.
    [Fact]
    public async Task UnaRespuestaFinalMalformadaSeCorrigeConUnReintento()
    {
        var modelo = new clsChatClientGuionizado()
            .Responder("fin del loop")
            .Responder("{\"tipo\": \"conversacional\", \"texto\": \"La cotización está lista\", \"requiere_confirmacion\": false, \"herramienta\": \"reservar\"}")
            .ResponderFinal("La cotización está lista para generar el PDF.");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, "900111222", conPdf: false);
        var sesion = SesionId();

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("¿En qué va?", sesion, cotizacion.Folio));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("texto").GetString().Should().Be("La cotización está lista para generar el PDF.");
        modelo.Llamadas.Should().HaveCount(3);
        modelo.Llamadas[1].Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        modelo.Llamadas[1].Herramientas.Should().BeEmpty();
        modelo.Llamadas[2].Mensajes[^1].Text.Should().Contain("herramienta");
        (await clsBaseDatosPruebas.EscalarAsync<string>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT TOP 1 contenido FROM dbo_cotifly.EventosConversacion WHERE spaceId = 'web/{sesion}' AND rol = 'assistant' ORDER BY createdAt DESC"))
            .Should().Be("La cotización está lista para generar el PDF.");
    }

    // HU-003.10 (#16): si el reintento también falla y no hubo propuesta, 200 RespuestaChatError con el texto de respaldo.
    [Fact]
    public async Task SiLaRespuestaFinalNoValidaNiConElReintentoEs200ConErrorYElRespaldo()
    {
        var modelo = new clsChatClientGuionizado().Responder("fin del loop").Responder("{\"tipo\": \"conversacional\", \"texto\": \"Hol").Responder("Hola, ¿qué revisamos?");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, conPdf: false);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId(), cotizacion.Folio));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("error").GetBoolean().Should().BeTrue();
        json.GetProperty("texto").GetString().Should().Be(Cotifly.Aplicacion.Agente.Services.clsTextosAgente.RespuestaNoEstructurada);
        modelo.Pendientes.Should().Be(0);
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
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, conPdf: false);

        using var limite = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId(), cotizacion.Folio));
        using var interno = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId(), cotizacion.Folio));

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
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, conPdf: false);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId(), cotizacion.Folio));

        (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK)).GetProperty("error").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ElLimiteDeLaApiResponde429ConErrorWeb()
    {
        var modelo = new clsChatClientGuionizado().Cerrar("uno");
        using var api = Api(modelo, new Dictionary<string, string?> { ["LimiteChat:PeticionesPorVentana"] = "1", ["LimiteChat:VentanaSegundos"] = "3600" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, conPdf: false);

        using var primera = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId(), cotizacion.Folio));
        using var segunda = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId(), cotizacion.Folio));

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        await VerificarContratoAsync(segunda, RutaChat, _post, HttpStatusCode.TooManyRequests);
    }

    // ADR-011 §3: el enlace firmado abre el PDF sin Authorization; alterado o de otro comercial, no. Lo emitía la tarjeta de Google Chat
    // (retirada en HU-013.9); la descarga se conserva mientras siga en el contrato.
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
