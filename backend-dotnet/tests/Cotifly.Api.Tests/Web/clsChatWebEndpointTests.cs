using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Infraestructura.Precio.Services;
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

    // HU-012.2: con el selector en USD el precio llega convertido con la TRM del día (DolarApi falso, sin red; la cadena por defecto). La
    // moneda se acepta sin distinguir mayúsculas (" usd ").
    [Fact]
    public async Task ConMonedaUsdElPrecioLlegaEnDolaresConLaTrm()
    {
        var modelo = new clsChatClientGuionizado()
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("Previsualización lista. ¿Confirmas?");
        using var trm = new clsTrmFalso().Caida(10);
        using var dolarApi = new clsDolarApiFalso { Valor = 3306.86m, FechaActualizacion = clsDolarApiFalso.HoyALasOchoEnBogota() };
        using var api = new clsApiWebPruebas(
            entorno.BaseDatos,
            configurar: b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IChatClient>(modelo);
                s.AddHttpClient(clsProveedorTrmSuperfinanciera.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => trm);
                s.AddHttpClient(clsProveedorTrmDolarApi.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => dolarApi);
            }));
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(
            _post, RutaChat, ana.Access, clsApiWebPruebas.Json(new { mensaje = "Cotiza Nacional, costo 2.850.000", sesion_id = SesionId(), moneda = " usd " }));

        var precio = (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK)).GetProperty("precio");
        precio.GetProperty("moneda").GetString().Should().Be("USD");
        precio.GetProperty("trm_aplicada").GetDecimal().Should().Be(3306.86m);
        // Política sembrada de Nacional: 15 % → 2.850.000 / 0,85 = 3.352.941 pesos; / 3.306,86 = US$1.013,93.
        precio.GetProperty("margen").GetDecimal().Should().Be(0.15m);
        precio.GetProperty("precio_cop").GetDecimal().Should().Be(3_352_941m);
        precio.GetProperty("precio").GetDecimal().Should().Be(1013.93m);
        precio.GetProperty("costo").GetDecimal().Should().Be(2_850_000m);
        precio.GetProperty("trm_fuente").GetString().Should().Be("dolarapi");
        dolarApi.Peticiones.Should().ContainSingle();
        trm.Peticiones.Should().BeEmpty("con DolarApi bien no se consulta el respaldo");
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    [InlineData("dólares")]
    public async Task UnaMonedaInvalidaEs400(string moneda)
    {
        using var api = Api(new clsChatClientGuionizado());
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(
            _post, RutaChat, ana.Access, clsApiWebPruebas.Json(new { mensaje = "hola", sesion_id = SesionId(), moneda }));

        (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.BadRequest)).GetProperty("error").GetString().Should().Be("moneda inválida");
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

    // HU-003.10 (#16): una respuesta final malformada se reintenta una vez con la corrección y llega la válida; con el JSON Schema.
    [Fact]
    public async Task UnaRespuestaFinalMalformadaSeCorrigeConUnReintento()
    {
        var modelo = new clsChatClientGuionizado()
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.9, metodo = "markup" })
            .Responder("fin del loop")
            .Responder("{\"tipo\": \"previsualizacion\", \"texto\": \"El precio final es $3.500.000\", \"requiere_confirmacion\": true, \"herramienta\": \"reservar\"}")
            .ResponderFinal("El precio final es $3.352.941. ¿Confirmas?", "previsualizacion", true, "confirmar_pdf");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var sesion = SesionId();

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Cotiza Nacional YUMBO a BUN, costo 2.850.000", sesion));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("texto").GetString().Should().Be("El precio final es $3.352.941. ¿Confirmas?");
        json.GetProperty("precio").GetProperty("precio").GetDecimal().Should().Be(3_352_941m);
        modelo.Llamadas.Should().HaveCount(5);
        modelo.Llamadas[3].Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        modelo.Llamadas[3].Herramientas.Should().BeEmpty();
        modelo.Llamadas[4].Mensajes[^1].Text.Should().Contain("herramienta");
        (await clsBaseDatosPruebas.EscalarAsync<string>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT TOP 1 contenido FROM dbo_cotifly.EventosConversacion WHERE spaceId = 'web/{sesion}' AND rol = 'assistant' ORDER BY createdAt DESC"))
            .Should().Be("El precio final es $3.352.941. ¿Confirmas?");
    }

    // HU-003.10 (#16): si el reintento también falla, 200 RespuestaChatError con el texto de respaldo; nada sin validar llega al comercial.
    [Fact]
    public async Task SiLaRespuestaFinalNoValidaNiConElReintentoEs200ConErrorYElRespaldo()
    {
        var modelo = new clsChatClientGuionizado().Responder("fin del loop").Responder("{\"tipo\": \"conversacional\", \"texto\": \"Hol").Responder("Hola, ¿qué cotizamos?");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("hola", SesionId()));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("error").GetBoolean().Should().BeTrue();
        json.GetProperty("texto").GetString().Should().Be(Cotifly.Aplicacion.Agente.Services.clsTextosAgente.RespuestaNoEstructurada);
        modelo.Pendientes.Should().Be(0);
    }

    // HU-003.10 (#16): con el respaldo, lo que ya calcularon las herramientas (precio, margen) no se pierde: RespuestaChat con el texto
    // de respaldo, así el comercial no repite el turno ni regenera el PDF.
    [Fact]
    public async Task ConElRespaldoLasSalidasDeLasHerramientasSiguenEnLaRespuesta()
    {
        var modelo = new clsChatClientGuionizado()
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.9, metodo = "markup" })
            .Responder("fin del loop")
            .Responder("{\"tipo\": \"previsualizacion\"")
            .Responder("El precio es $9.999.999");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Cotiza Nacional YUMBO a BUN, costo 2.850.000", SesionId()));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.TryGetProperty("error", out _).Should().BeFalse("es RespuestaChat, no RespuestaChatError");
        json.GetProperty("texto").GetString().Should().Be(Cotifly.Aplicacion.Agente.Services.clsTextosAgente.RespuestaNoEstructurada);
        json.GetProperty("precio").GetProperty("precio").GetDecimal().Should().Be(3_352_941m);
        json.GetProperty("margen").GetProperty("origen").GetString().Should().Be("politica_linea");
        json.GetProperty("herramientas").EnumerateArray().Select(h => h.GetString()).Should().Equal("obtener_margen", "calcular_precio");
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
