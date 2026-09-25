using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Cotifly.Aceptacion.Tests.Agente;

/// <summary>
/// Estado del agente en un escenario: el modelo guionizado, el catálogo de clientes falso, el Google falso y los identificadores
/// únicos (sesiones web, space e hilos de Chat) para que los escenarios no choquen en la base compartida.
/// </summary>
public sealed class clsContextoAgente : IDisposable
{
    private readonly string _sufijo = Guid.NewGuid().ToString("N")[..8];

    public clsChatClientGuionizado Modelo { get; } = new();

    public clsBuscadorClientesFalso Buscador { get; } = new();

    public clsGoogleFalso Google { get; } = new();

    public string Space => $"spaces/S{_sufijo}";

    public Dictionary<string, string> SesionesChat { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HttpStatusCode EstadoWebhook { get; set; }

    public string CuerpoWebhook { get; set; } = string.Empty;

    public string Hilo(string alias) => $"{Space}/threads/{alias}";

    /// <summary><c>sesion_id</c> real de un alias: único por escenario si tiene forma válida; tal cual si no (para probar el 400).</summary>
    public string SesionWeb(string alias) =>
        Regex.IsMatch(alias, "^[A-Za-z0-9_-]{1,40}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ? $"{alias}-{_sufijo}" : alias;

    public void Dispose() => Google.Dispose();
}

/// <summary>Catálogo de clientes falso (reemplaza al stub de HU-005.2): coincidencia exacta por nombre sin mayúsculas.</summary>
public sealed class clsBuscadorClientesFalso : IClsBuscadorClientes
{
    public List<clsClienteEncontrado> Clientes { get; } = [];

    public bool Disponible { get; set; } = true;

    public Task<clsResultado<clsResultadoBusquedaClientes>> BuscarAsync(string texto, CancellationToken cancellationToken = default)
    {
        if (!Disponible)
        {
            return Task.FromResult(clsResultado<clsResultadoBusquedaClientes>.Fallo(new clsErrorDominio(clsCodigosError.ClienteBusquedaNoDisponible, "Búsqueda no disponible.")));
        }

        var encontrados = Clientes.Where(c => c.Nombre.Contains(texto, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(clsResultado<clsResultadoBusquedaClientes>.Exito(new clsResultadoBusquedaClientes(encontrados.Count == 1, encontrados)));
    }
}

/// <summary>
/// Pasos de <c>chat_slot_filling.feature</c>, <c>chat_web.feature</c> y <c>google_chat.feature</c> (#12 parte C): la API real en
/// memoria con el modelo guionizado, el catálogo de clientes falso y Google falso; aserciones contra el contrato y la base.
/// </summary>
[Binding]
public sealed class clsPasosAgente(clsContextoWeb web, clsContextoAgente agente)
{
    private static readonly object _payloadSinClienteNiNumeros = new
    {
        cliente = "Cliente del chat",
        linea_negocio = "nacional",
        textos = new { intro = new[] { "Gracias por confiar en Botero Soto." } },
        tarifa = new { titulo = "COTIZACIÓN TRANSPORTE NACIONAL", origen = "YUMBO", filas = new[] { new { destino = "BUENAVENTURA", vehiculo_peso = "Tractomula - 34 TON" } } },
    };

    private static decimal Numero(string texto) => decimal.Parse(texto, NumberStyles.Number, CultureInfo.InvariantCulture);

    private void Configurar(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IChatClient>(agente.Modelo);
            s.AddScoped<IClsBuscadorClientes>(_ => agente.Buscador);
            s.AddHttpClient(clsVerificadorJwtGoogleChat.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => agente.Google);
        });

    [Given("la API web de Cotifly con el agente guionizado")]
    public void DadoLaApiConElAgente() => web.Iniciar(null, Configurar);

    [Given("la API web de Cotifly con el agente guionizado y SAP TM de muestra")]
    public void DadoLaApiConElAgenteYSap() => web.Iniciar(
        new Dictionary<string, string?> { ["SapTm:Modo"] = "export_json", ["SapTm:ExportPath"] = clsSapTmFalso.RutaMuestra },
        Configurar);

    [Given("la API de Cotifly conectada a Google Chat con el agente guionizado")]
    public void DadoLaApiConGoogleChat() => web.Iniciar(agente.Google.Configuracion(), Configurar);

    [Given("el catálogo de clientes tiene a {string} con NIT {string} y BP {string}")]
    public void DadoElCatalogo(string nombre, string nit, string bp) => agente.Buscador.Clientes.Add(new clsClienteEncontrado(nit, nombre, "cliente", bp));

    [Given("que la búsqueda de clientes no está disponible")]
    public void DadoSinBusqueda() => agente.Buscador.Disponible = false;

    [Given(@"^que en el turno (\d+) el modelo busca al cliente ""([^""]+)"", pide el margen de ""([^""]+)"" para el cliente ""([^""]+)"" y calcula el precio con costo (\d+) y margen ([0-9.]+)$")]
    public void DadoTurnoConCliente(int turno, string cliente, string linea, string clienteMargen, long costo, string margen) =>
        agente.Modelo
            .Llamar("buscar_cliente", new { nombre = cliente })
            .Llamar("obtener_margen", new { lineaNegocio = linea, cliente = clienteMargen })
            .Llamar("calcular_precio", new { costo, margen = Numero(margen), metodo = "markup" })
            .Cerrar($"Turno {turno}: previsualización lista. ¿Confirmas?");

    [Given(@"^que en el turno (\d+) el modelo pide el margen de ""([^""]+)"" y calcula el precio con costo (\d+) y margen ([0-9.]+)$")]
    public void DadoTurnoSinCliente(int turno, string linea, long costo, string margen) =>
        agente.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = linea })
            .Llamar("calcular_precio", new { costo, margen = Numero(margen), metodo = "markup" })
            .Cerrar($"Turno {turno}: previsualización lista. ¿Confirmas?");

    [Given(@"^que en el turno (\d+) el modelo pide el margen de ""([^""]+)"", calcula el precio con costo (\d+) y genera el PDF de una vez$")]
    public void DadoTurnoConPdfDeUnaVez(int turno, string linea, long costo) =>
        agente.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = linea })
            .Llamar("calcular_precio", new { costo, margen = 0.2m, metodo = "margen" })
            .Llamar("generar_pdf", new { cotizacion = _payloadSinClienteNiNumeros })
            .Cerrar($"Turno {turno}: necesito tu confirmación.");

    [Given(@"^que en el turno (\d+) el modelo genera el PDF sin cliente ni números$")]
    public void DadoTurnoPdf(int turno) =>
        agente.Modelo.Llamar("generar_pdf", new { cotizacion = _payloadSinClienteNiNumeros }).Cerrar($"Turno {turno}: el PDF está listo.");

    [Given("que el modelo responde {string}")]
    public void DadoElModeloResponde(string texto) => agente.Modelo.Cerrar(texto);

    [Given("que el proveedor de IA responde con el límite de peticiones")]
    public void DadoLimite() => agente.Modelo.Fallar(new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests));

    [When("{string} escribe en el chat {string}")]
    public Task CuandoEscribe(string alias, string mensaje) => EscribirAsync(alias, mensaje, agente.SesionWeb($"sesion-{alias}"));

    [When("{string} escribe en el chat {string} en la sesión {string}")]
    public Task CuandoEscribeEnSesion(string alias, string mensaje, string sesion) => EscribirAsync(alias, mensaje, agente.SesionWeb(sesion));

    [Then("la respuesta del chat trae el margen {string} con origen {string} y el precio {string}")]
    public void EntoncesMargenYPrecio(string margen, string origen, string precio)
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var json = web.UltimoJson();
        json.GetProperty("margen").GetProperty("margen").GetDecimal().Should().Be(Numero(margen));
        json.GetProperty("margen").GetProperty("origen").GetString().Should().Be(origen);
        json.GetProperty("precio").GetProperty("precio").GetDecimal().Should().Be(Numero(precio));
        json.GetProperty("precio").GetProperty("margen").GetDecimal().Should().Be(Numero(margen), "el precio usa el margen del estado, no el del LLM");
    }

    [Then("la respuesta del chat lista las herramientas {string}")]
    public void EntoncesHerramientas(string herramientas) =>
        web.UltimoJson().GetProperty("herramientas").EnumerateArray().Select(h => h.GetString()).Should().Equal(herramientas.Split(", "));

    [Then("el texto del chat empieza por {string}")]
    public void EntoncesTexto(string inicio) => web.UltimoJson().GetProperty("texto").GetString().Should().StartWith(inicio);

    [Then("la respuesta del chat trae el PDF con un folio del NIT {string}")]
    public void EntoncesPdfConNit(string nit) => FolioDelPdf().Should().MatchRegex($"^PCN-{nit}-[0-9]{{8}}-[0-9]{{3}}$");

    [Then("la respuesta del chat trae el PDF con un folio sin NIT")]
    public void EntoncesPdfSinNit() => FolioDelPdf().Should().MatchRegex("^PCN-[0-9]{8}-[0-9]{3}$");

    [Then(@"^la cotización del PDF quedó guardada para ""([^""]+)"" con el NIT ""([^""]+)"" de origen ""([^""]+)"", la línea ""([^""]+)"", el margen ""([^""]+)"" de origen ""([^""]+)"" y el precio ""([^""]+)""$")]
    public Task EntoncesGuardadaConNit(string alias, string nit, string origenCliente, string linea, string margen, string origenMargen, string precio) =>
        VerificarCotizacionAsync(alias, nit, origenCliente, linea, margen, origenMargen, precio);

    [Then(@"^la cotización del PDF quedó guardada para ""([^""]+)"" sin NIT, con la línea ""([^""]+)"", el margen ""([^""]+)"" de origen ""([^""]+)"" y el precio ""([^""]+)""$")]
    public Task EntoncesGuardadaSinNit(string alias, string linea, string margen, string origenMargen, string precio) =>
        VerificarCotizacionAsync(alias, string.Empty, string.Empty, linea, margen, origenMargen, precio);

    [Then("la herramienta {string} respondió al modelo el código {string}")]
    public void EntoncesLaHerramientaRespondio(string herramienta, string codigo)
    {
        var mensajes = agente.Modelo.Llamadas[^1].Mensajes;
        var llamadas = mensajes.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Where(l => l.Name == herramienta).Select(l => l.CallId).ToHashSet();
        var resultados = mensajes.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Where(r => llamadas.Contains(r.CallId)).ToList();
        resultados.Should().NotBeEmpty($"el modelo pidió {herramienta}");
        JsonDocument.Parse(resultados[^1].Result!.ToString()!).RootElement.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
    }

    [Then("{string} no tiene cotizaciones guardadas")]
    public async Task EntoncesSinCotizaciones(string alias) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE usuarioId = '{web.Sesion(alias).UsuarioId}'")).Should().Be(0);

    [When("Google Chat envía el mensaje {string} de {string} en el hilo {string}")]
    public Task CuandoGoogleChatEnvia(string mensaje, string remitente, string hilo) => EnviarEventoAsync(mensaje, remitente, hilo, jwtValido: true);

    [When("Google Chat envía el mensaje {string} de {string} en el hilo {string} con un JWT inválido")]
    public Task CuandoGoogleChatEnviaConJwtInvalido(string mensaje, string remitente, string hilo) => EnviarEventoAsync(mensaje, remitente, hilo, jwtValido: false);

    private async Task EnviarEventoAsync(string mensaje, string remitente, string hilo, bool jwtValido)
    {
        var correo = remitente.Contains('@', StringComparison.Ordinal) ? remitente : $"{web.Sesion(remitente).NombreUsuario}@boterosoto.com.co";
        var evento = new JsonObject
        {
            ["type"] = "MESSAGE",
            ["message"] = new JsonObject
            {
                ["text"] = mensaje,
                ["sender"] = new JsonObject { ["email"] = correo, ["displayName"] = remitente },
                ["thread"] = new JsonObject { ["name"] = agente.Hilo(hilo) },
                ["space"] = new JsonObject { ["name"] = agente.Space },
            },
            ["space"] = new JsonObject { ["name"] = agente.Space },
        };
        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/chat/events") { Content = new StringContent(evento.ToJsonString(), Encoding.UTF8, "application/json") };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clsGoogleFalso.Fixture(jwtValido ? "jwt_valido.txt" : "jwt_invalido.txt"));
        using var respuesta = await web.Api.Cliente.SendAsync(peticion);
        agente.EstadoWebhook = respuesta.StatusCode;
        agente.CuerpoWebhook = await respuesta.Content.ReadAsStringAsync();
    }

    [Then("el webhook acusa recibo")]
    public void EntoncesAcusa()
    {
        agente.EstadoWebhook.Should().Be(HttpStatusCode.OK, agente.CuerpoWebhook);
        JsonNode.Parse(agente.CuerpoWebhook)!["text"]!.GetValue<string>().Should().Be(clsTextosAgente.Recibo);
    }

    [Then("el webhook responde {int}")]
    public void EntoncesWebhookResponde(int estado) => agente.EstadoWebhook.Should().Be((HttpStatusCode)estado);

    [Then("en el hilo {string} se publica el texto {string}")]
    public async Task EntoncesSePublicaTexto(string hilo, string texto) =>
        (await PublicadoEnAsync(hilo))["text"]!.GetValue<string>().Should().Be(texto);

    [Then("en el hilo {string} se publica la previsualización con el precio {string} y el botón {string}")]
    public async Task EntoncesSePublicaPrevisualizacion(string hilo, string precio, string boton)
    {
        var mensaje = await PublicadoEnAsync(hilo);
        var widgets = mensaje["cardsV2"]![0]!["card"]!["sections"]![0]!["widgets"]!.AsArray();
        widgets.Where(w => w!["decoratedText"] is not null)
            .ToDictionary(w => w!["decoratedText"]!["topLabel"]!.GetValue<string>(), w => w!["decoratedText"]!["text"]!.GetValue<string>())["Precio"].Should().Be(precio);
        widgets.Where(w => w!["buttonList"] is not null).SelectMany(w => w!["buttonList"]!["buttons"]!.AsArray()).Select(b => b!["text"]!.GetValue<string>())
            .Should().Contain(boton);
        mensaje["text"]!.GetValue<string>().Should().Be(clsTarjetasChat.PromptConfirmacion);
    }

    [Then("la sesión del hilo {string} quedó ligada a {string}")]
    public async Task EntoncesSesionLigada(string hilo, string alias)
    {
        var usuario = await clsBaseDatosPruebas.EscalarAsync<Guid>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT usuarioId FROM dbo_cotifly.Sesiones WHERE spaceId = '{agente.Space}' AND threadId = '{agente.Hilo(hilo)}'");
        usuario.Should().Be(web.Sesion(alias).UsuarioId);
    }

    [Then("el modelo no recibió ninguna llamada")]
    public void EntoncesSinLlamadas() => agente.Modelo.Llamadas.Should().BeEmpty();

    [Then("no se publicó nada en Google Chat")]
    public async Task EntoncesNadaPublicado()
    {
        await Task.Delay(500);
        agente.Google.Peticiones.Where(p => p.Url.AbsolutePath.Contains("/spaces/", StringComparison.Ordinal)).Should().BeEmpty();
    }

    private Task EscribirAsync(string alias, string mensaje, string sesion) =>
        web.EnviarAsync(HttpMethod.Post, "/web/chat", web.Sesion(alias).Access, clsApiWebPruebas.Json(new { mensaje, sesion_id = sesion }));

    private string FolioDelPdf()
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        return web.UltimoJson().GetProperty("pdf").GetProperty("folio").GetString()!;
    }

    private async Task VerificarCotizacionAsync(string alias, string nit, string origenCliente, string linea, string margen, string origenMargen, string precio)
    {
        var id = web.UltimoJson().GetProperty("pdf").GetProperty("cotizacion_id").GetString();
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly, $"""
            SELECT CONCAT(CAST(c.usuarioId AS NVARCHAR(36)), '|', ISNULL(c.nitCliente, ''), '|', ISNULL(c.origenCliente, ''), '|', m.lineaNegocio, '|',
                          CAST(c.margenAplicado AS NVARCHAR(30)), '|', c.origenMargen, '|', CAST(c.precioFinal AS NVARCHAR(30)))
            FROM dbo_cotifly.Cotizaciones c JOIN dbo_cotifly.MargenesPolitica m ON m.id = c.lineaNegocioId
            WHERE c.id = '{id}'
            """);
        var partes = fila!.Split('|');
        Guid.Parse(partes[0]).Should().Be(web.Sesion(alias).UsuarioId);
        partes[1].Should().Be(nit);
        partes[2].Should().Be(origenCliente);
        partes[3].Should().Be(linea);
        Numero(partes[4]).Should().Be(Numero(margen));
        partes[5].Should().Be(origenMargen);
        Numero(partes[6]).Should().Be(Numero(precio));
    }

    /// <summary>Espera (hasta 15 s) a que el worker publique en el hilo y devuelve el cuerpo del mensaje.</summary>
    private async Task<JsonObject> PublicadoEnAsync(string hilo)
    {
        var nombre = agente.Hilo(hilo);
        for (var intento = 0; intento < 150; intento++)
        {
            var publicado = agente.Google.Peticiones
                .Where(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath == $"/v1/{agente.Space}/messages" && p.Cuerpo is not null)
                .Select(p => JsonNode.Parse(p.Cuerpo!)!.AsObject())
                .FirstOrDefault(c => c["thread"]?["name"]?.GetValue<string>() == nombre);
            if (publicado is not null)
            {
                return publicado;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No se publicó nada en el hilo {nombre}.");
    }
}
