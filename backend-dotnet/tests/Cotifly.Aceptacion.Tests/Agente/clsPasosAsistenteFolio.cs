using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Aceptacion.Tests.Costeo;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Cotifly.Aceptacion.Tests.Agente;

/// <summary>
/// Pasos de <c>specs/features/asistente_folio.feature</c> (HU-013.6, #102, ADR-018 §2b): la API real en memoria conectada al Nexo falso
/// de <see cref="clsPasosCosteo"/>, cuyo <see cref="clsPasosCosteo.Modelo"/> guionizado es el <c>IChatClient</c> del asistente. Guarda
/// cada respuesta del chat para verificar la acción propuesta, confirmarla como el frontend y comprobar que ninguna lleva montos.
/// </summary>
[Binding]
public sealed partial class clsPasosAsistenteFolio(clsContextoWeb web, clsPasosCosteo costeo, clsPasosAjusteMargen ajuste)
{
    private const string RutaChat = "/web/chat";
    private readonly string _sesion = Guid.NewGuid().ToString("N");
    private readonly List<string> _respuestasChat = [];
    private readonly Dictionary<string, string> _foliosChat = new(StringComparer.OrdinalIgnoreCase);

    private const string TextoPropuesta = "Te propongo este cambio. Revisa el resumen y, si estás de acuerdo, confírmalo con el botón.";

    // --- Guion del modelo ---

    [Given("que el modelo consulta la cotización y responde {string}")]
    public void DadoQueConsultaYResponde(string texto) =>
        costeo.Modelo.Llamar("consultar_cotizacion", new { }).Cerrar(texto);

    [Given("que el modelo propone ajustar el margen a {string}")]
    public void DadoQueProponeElMargen(string margen) =>
        Proponer(costeo.Modelo.Llamar("proponer_ajuste_margen", new { margen = Decimal(margen) }));

    [Given("que el modelo propone ajustar el margen a {string} con la justificación {string}")]
    public void DadoQueProponeElMargenConJustificacion(string margen, string justificacion) =>
        Proponer(costeo.Modelo.Llamar("proponer_ajuste_margen", new { margen = Decimal(margen), justificacion }));

    [Given("que el modelo consulta la cotización y propone ajustar el margen a {string}")]
    public void DadoQueConsultaYPropone(string margen) =>
        Proponer(costeo.Modelo.Llamar("consultar_cotizacion", new { }).Llamar("proponer_ajuste_margen", new { margen = Decimal(margen) }));

    [Given("que el catálogo de Nexo tiene el servicio conexo {string} con el id {string}")]
    public void DadoElServicioDelCatalogo(string nombre, string id) =>
        costeo.Nexo.ConServicioConexo(id, "ALM-01", nombre, "logistico", "Día", 50_000m);

    [Given("que el modelo busca el servicio conexo {string} y propone agregarlo con la cantidad {int}")]
    public void DadoQueBuscaElConexo(string texto, int cantidad) =>
        Proponer(costeo.Modelo.Llamar("buscar_servicio_conexo", new { texto, cantidad }));

    [Given("que el modelo propone re-solicitar el costeo con el motivo {string} y el peso {int}")]
    public void DadoQueProponeReSolicitar(string motivo, int peso) =>
        Proponer(costeo.Modelo.Llamar("proponer_re_solicitud", new { motivo, pesoKg = peso }));

    [Given("que el modelo propone re-solicitar el costeo con el motivo {string} sin cambios de carga")]
    public void DadoQueProponeReSolicitarSinCambios(string motivo) =>
        Proponer(costeo.Modelo.Llamar("proponer_re_solicitud", new { motivo }));

    [Given("que el modelo intenta re-solicitar el costeo con el motivo {string} y el peso {int}")]
    public void DadoQueIntentaReSolicitar(string motivo, int peso) =>
        costeo.Modelo.Llamar("proponer_re_solicitud", new { motivo, pesoKg = peso })
            .Cerrar("El peso no cuadra con las posiciones de la carga: ajústalo o actualiza las posiciones desde el detalle de la cotización.");

    [Given("que el modelo pide la herramienta {string}")]
    public void DadoQuePideLaHerramienta(string herramienta) =>
        costeo.Modelo.Llamar(herramienta, new { costo = 2_550_000, margen = 0.15 }).Cerrar("Esa acción no está disponible en el asistente.");

    [Given("que la creación por chat se retiró")]
    public void DadoQueLaCreacionSeRetiro() =>
        typeof(clsAsistenteFolioRunner).Assembly.GetType("Cotifly.Aplicacion.Agente.Services.IClsAgenteRunner")
            .Should().BeNull("el agente cotizador legado y el flag Chat:CreacionHabilitada se retiraron en HU-013.9 (#105)");

    [Given("que Nexo costeó la solicitud de {string} con el conexo {string} de {int}")]
    public Task DadoElConexo(string cotizacion, string conexo, int valor) => ajuste.DadoElConexo(cotizacion, conexo, valor);

    [Given("que {string} tiene la cotización de chat {string} creada antes de HU-013")]
    public async Task DadoUnaCotizacionDeChat(string alias, string folio)
    {
        // Como las crea el agente legado (HU-003): folio del servidor, sin estadoCosteo. El alias del .feature apunta al folio real.
        var cotizacion = await web.Api.CrearCotizacionAsync(web.Sesion(alias).UsuarioId, "900555111", conPdf: false);
        (await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT estadoCosteo FROM dbo_cotifly.Cotizaciones WHERE folio = '{cotizacion.Folio}'")).Should().BeNull();
        _foliosChat[folio] = cotizacion.Folio;
    }

    // --- Chat ---

    [When("{string} escribe en el chat {string} sin folio")]
    public Task CuandoEscribeSinFolio(string alias, string mensaje) =>
        ChatAsync(alias, clsApiWebPruebas.Json(new { mensaje, sesion_id = _sesion }));

    [When("{string} escribe en el chat {string} sobre el folio de {string}")]
    public Task CuandoEscribe(string alias, string mensaje, string cotizacion) =>
        ChatAsync(alias, clsApiWebPruebas.Json(new { mensaje, sesion_id = _sesion, folio = costeo.Folio(cotizacion) }));

    [Given("{string} escribió en el chat {string} sobre el folio de {string}")]
    public async Task DadoQueEscribio(string alias, string mensaje, string cotizacion)
    {
        await CuandoEscribe(alias, mensaje, cotizacion);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        Accion().ValueKind.Should().Be(JsonValueKind.Object, web.UltimoCuerpo);
    }

    [When("{string} confirma la acción propuesta")]
    public async Task CuandoConfirma(string alias)
    {
        // Como el frontend: método, endpoint y cuerpo tal cual, If-Match con el row_version de la propuesta y una Idempotency-Key
        // nueva por clic (la usan re-solicitar y conexos; el margen la ignora).
        var accion = JsonDocument.Parse(_respuestasChat[^1]).RootElement.GetProperty("accion_propuesta");
        await web.EnviarAsync(
            new HttpMethod(accion.GetProperty("metodo").GetString()!),
            accion.GetProperty("endpoint").GetString()!,
            web.Sesion(alias).Access,
            accion.GetProperty("cuerpo").GetRawText(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["If-Match"] = $"\"{accion.GetProperty("row_version").GetString()}\"",
                ["Idempotency-Key"] = Guid.NewGuid().ToString(),
            });
    }

    [When("{string} consulta \"Mis cotizaciones\"")]
    public Task CuandoConsultaMisCotizaciones(string alias) =>
        web.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", web.Sesion(alias).Access);

    // --- Verificaciones ---

    [Then("la respuesta del chat no trae acción propuesta")]
    public void EntoncesSinAccion() => Accion().ValueKind.Should().Be(JsonValueKind.Null);

    [Then("las herramientas del turno son {string}")]
    public void EntoncesLasHerramientas(string herramientas) =>
        web.UltimoJson().GetProperty("herramientas").EnumerateArray().Select(h => h.GetString())
            .Should().Equal(herramientas.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    [Then("la acción propuesta es {string} con {string} al endpoint del margen de {string} y el cuerpo con el margen {string}")]
    public void EntoncesLaAccionDeMargen(string tipo, string metodo, string cotizacion, string margen)
    {
        var accion = Verificar(tipo, metodo, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo/margen");
        accion.GetProperty("cuerpo").GetProperty("margen").GetDecimal().Should().Be(Decimal(margen));
    }

    [Then("la acción propuesta es {string} con {string} al endpoint de servicios conexos de {string}")]
    public void EntoncesLaAccionDeConexos(string tipo, string metodo, string cotizacion) =>
        Verificar(tipo, metodo, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo/servicios-conexos");

    [Then("la acción propuesta es {string} con {string} al endpoint de re-solicitud de {string}")]
    public void EntoncesLaAccionDeReSolicitud(string tipo, string metodo, string cotizacion) =>
        Verificar(tipo, metodo, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo/re-solicitar");

    [Then("el resumen de la acción propuesta menciona {string}")]
    public void EntoncesElResumenMenciona(string texto) =>
        Accion().GetProperty("resumen").GetString().Should().Contain(texto, "el comercial ve lo que confirma");

    [Then("el cuerpo de la acción propuesta no trae datos de carga")]
    public void EntoncesSinDatosDeCarga()
    {
        var cuerpo = Accion().GetProperty("cuerpo");
        cuerpo.TryGetProperty("datos_carga", out _).Should().BeFalse();
        cuerpo.GetProperty("nueva_version").GetBoolean().Should().BeFalse("sin cambios de carga se pide en el mismo folio");
    }

    [Then("el resumen de la acción propuesta menciona {string} y el precio {int}")]
    public void EntoncesElResumen(string texto, int precio)
    {
        var resumen = Accion().GetProperty("resumen").GetString()!;
        resumen.Should().Contain(texto);
        SinSeparadores(resumen).Should().Contain(precio.ToString(CultureInfo.InvariantCulture));
    }

    [Then("la acción propuesta trae el row_version actual de {string}")]
    public async Task EntoncesElRowVersion(string cotizacion) =>
        Accion().GetProperty("row_version").GetString().Should().Be(await costeo.Columna(cotizacion, "versionNegocio"));

    [Then("el cuerpo de la acción propuesta trae la justificación {string}")]
    public void EntoncesLaJustificacion(string justificacion) =>
        Accion().GetProperty("cuerpo").GetProperty("justificacion").GetString().Should().Be(justificacion);

    [Then("el cuerpo de la acción propuesta trae en la línea {int} el servicio {string} con la cantidad {int}")]
    public void EntoncesElItem(int linea, string servicio, int cantidad)
    {
        var item = Accion().GetProperty("cuerpo").GetProperty("items").EnumerateArray().Single();
        item.GetProperty("linea_orden").GetInt32().Should().Be(linea);
        item.GetProperty("servicio_id").GetString().Should().Be(servicio);
        item.GetProperty("cantidad").GetDecimal().Should().Be(cantidad);
    }

    [Then("el cuerpo de la acción propuesta trae el motivo {string} y el peso {int}")]
    public void EntoncesElMotivoYElPeso(string motivo, int peso)
    {
        var cuerpo = Accion().GetProperty("cuerpo");
        cuerpo.GetProperty("motivo").GetString().Should().Be(motivo);
        cuerpo.GetProperty("datos_carga").GetProperty("peso_kg").GetDecimal().Should().Be(peso);
        cuerpo.GetProperty("nueva_version").GetBoolean().Should().BeTrue("con cambios de carga siempre es versión nueva");
    }

    [Then("ninguna respuesta del chat contiene el campo {string}")]
    public void EntoncesSinCampo(string campo) =>
        _respuestasChat.Should().NotBeEmpty().And.OnlyContain(r => !r.Contains($"\"{campo}\"", StringComparison.Ordinal));

    [Then("el error de herramienta del turno es {string}")]
    public void EntoncesElError(string codigo) =>
        web.UltimoJson().GetProperty("errores").EnumerateArray().Select(e => e.GetProperty("codigo").GetString()).Should().Equal(codigo);

    [Then("{string} tiene {int} cotización en \"Mis cotizaciones\"")]
    public async Task EntoncesTieneCotizaciones(string alias, int total)
    {
        await CuandoConsultaMisCotizaciones(alias);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK);
        web.UltimoJson().GetProperty("cotizaciones").GetArrayLength().Should().Be(total);
    }

    [Then("\"Mis cotizaciones\" de {string} incluye {string}")]
    public void EntoncesIncluye(string alias, string folio)
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, $"consulta de {alias}");
        web.UltimoJson().GetProperty("cotizaciones").EnumerateArray().Select(c => c.GetProperty("folio").GetString())
            .Should().Contain(_foliosChat[folio]);
    }

    [Then("la salida de {string} que recibió el modelo trae los márgenes {string} y {string}, el estado, la ruta, la línea, la moneda y el conexo {string}")]
    public void EntoncesLaSalidaDeConsultar(string herramienta, string actual, string sugerido, string conexo)
    {
        var salida = ResultadosAlModelo(herramienta).Should().ContainSingle().Subject;
        using var json = JsonDocument.Parse(salida);
        var raiz = json.RootElement;
        raiz.GetProperty("margen_actual").GetDecimal().Should().Be(Decimal(actual));
        raiz.GetProperty("margen_sugerido").GetDecimal().Should().Be(Decimal(sugerido));
        raiz.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        raiz.GetProperty("origen").GetString().Should().NotBeNullOrWhiteSpace();
        raiz.GetProperty("destino").GetString().Should().NotBeNullOrWhiteSpace();
        raiz.GetProperty("linea_negocio").GetString().Should().NotBeNullOrWhiteSpace();
        raiz.GetProperty("moneda").GetString().Should().Be("COP");
        raiz.GetProperty("conexos").EnumerateArray().Select(c => c.GetProperty("nombre").GetString()).Should().Contain(conexo);
        foreach (var prohibido in new[] { "precio_final", "costo", "precio", "trm_aplicada", "valor_mercancia", "valor_estimado", "total" })
        {
            raiz.TryGetProperty(prohibido, out _).Should().BeFalse($"'{prohibido}' es un monto y nunca va al modelo");
        }
    }

    [Then("ningún mensaje enviado al modelo contiene {string}, {string}, {string}, {string} ni {string}")]
    public void EntoncesNingunMensajeContiene(string uno, string dos, string tres, string cuatro, string cinco)
    {
        var enviado = string.Join("\n", costeo.Modelo.Llamadas.SelectMany(l => l.Mensajes).SelectMany(m => m.Contents.Select(Texto).Append(m.Text)));
        enviado.Should().NotBeEmpty();
        foreach (var prohibido in new[] { uno, dos, tres, cuatro, cinco })
        {
            enviado.Should().NotContainEquivalentOf(prohibido);
            SinSeparadores(enviado).Should().NotContainEquivalentOf(prohibido, "tampoco con separadores de miles");
        }
    }

    [Then("ninguna respuesta del chat contiene el campo {string} ni los valores {int} ni {int}")]
    public void EntoncesNingunaRespuestaContiene(string campo, int uno, int dos)
    {
        _respuestasChat.Should().NotBeEmpty();
        foreach (var respuesta in _respuestasChat)
        {
            respuesta.Should().NotContain($"\"{campo}\"");
            foreach (var valor in new[] { uno, dos })
            {
                SinSeparadores(respuesta).Should().NotContain(valor.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    [Then("los campos precio, margen y pdf de la respuesta del chat son null")]
    public void EntoncesObsoletosEnNull()
    {
        var json = JsonDocument.Parse(_respuestasChat[^1]).RootElement;
        foreach (var campo in new[] { "precio", "margen", "pdf" })
        {
            json.GetProperty(campo).ValueKind.Should().Be(JsonValueKind.Null, $"'{campo}' es obsoleto desde HU-013.6");
        }
    }

    [Then("el resumen de la acción propuesta lo armó el servidor con el precio {int}")]
    public void EntoncesElResumenDelServidor(int precio)
    {
        var json = JsonDocument.Parse(_respuestasChat[^1]).RootElement;
        var resumen = json.GetProperty("accion_propuesta").GetProperty("resumen").GetString()!;
        var texto = precio.ToString(CultureInfo.InvariantCulture);
        SinSeparadores(resumen).Should().Contain(texto, "el resumen con montos lo arma el servidor");
        SinSeparadores(json.GetProperty("texto").GetString()!).Should().NotContain(texto, "el texto es del LLM, que nunca ve los montos");
        costeo.Modelo.Llamadas.SelectMany(l => l.Mensajes).Select(m => SinSeparadores(m.Text)).Should().NotContain(t => t.Contains(texto));
    }

    private static void Proponer(clsChatClientGuionizado modelo) =>
        modelo.Proponer(TextoPropuesta);

    private async Task ChatAsync(string alias, string cuerpo)
    {
        await web.EnviarAsync(HttpMethod.Post, RutaChat, web.Sesion(alias).Access, cuerpo);
        _respuestasChat.Add(web.UltimoCuerpo);
    }

    private JsonElement Accion() => JsonDocument.Parse(_respuestasChat[^1]).RootElement.GetProperty("accion_propuesta");

    private JsonElement Verificar(string tipo, string metodo, string endpoint)
    {
        var accion = Accion();
        accion.GetProperty("tipo").GetString().Should().Be(tipo);
        accion.GetProperty("metodo").GetString().Should().Be(metodo);
        accion.GetProperty("endpoint").GetString().Should().Be(endpoint);
        accion.GetProperty("resumen").GetString().Should().NotBeNullOrWhiteSpace();
        return accion;
    }

    private IEnumerable<string> ResultadosAlModelo(string herramienta)
    {
        // Resultado de cada llamada a la herramienta, una sola vez (cada llamada al modelo repite los mensajes anteriores).
        var llamadas = costeo.Modelo.Llamadas.SelectMany(l => l.Mensajes).SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Where(c => c.Name == herramienta).Select(c => c.CallId).ToHashSet(StringComparer.Ordinal);
        return costeo.Modelo.Llamadas.SelectMany(l => l.Mensajes).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Where(r => llamadas.Contains(r.CallId))
            .GroupBy(r => r.CallId)
            .Select(g => g.First().Result?.ToString() ?? string.Empty);
    }

    private static string Texto(AIContent contenido) => contenido switch
    {
        TextContent texto => texto.Text,
        FunctionCallContent llamada => llamada.Name + JsonSerializer.Serialize(llamada.Arguments),
        FunctionResultContent resultado => resultado.Result?.ToString() ?? string.Empty,
        _ => string.Empty,
    };

    private static decimal Decimal(string texto) => decimal.Parse(texto, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string SinSeparadores(string? texto) => SeparadorMiles().Replace(texto ?? string.Empty, string.Empty);

    [GeneratedRegex(@"(?<=\d)[.,](?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SeparadorMiles();
}
