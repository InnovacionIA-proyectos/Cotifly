using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Redaccion.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.AI;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/redaccion_cotizacion.feature</c> (HU-013.5, #101, ADR-018 §2a): la API real en memoria con SQL Server 2019,
/// el Nexo falso de <see cref="clsPasosCosteo"/> y su <see cref="clsChatClientGuionizado"/> (nunca un proveedor real). Las mutaciones
/// envían <c>If-Match</c> con la versión vigente de la fila salvo que el paso diga otra cosa.
/// </summary>
[Binding]
public sealed partial class clsPasosRedaccionCotizacion(clsContextoWeb web, clsPasosCosteo costeo, clsPasosAjusteMargen margen)
{
    private const string IntroduccionValida =
        "Apreciado cliente: agradecemos la oportunidad de presentarle nuestra propuesta de transporte para su operación.";

    private clsChatClientGuionizado Modelo => costeo.Modelo;

    [Given("que el modelo responde el asunto {string} y una introducción válida")]
    public void DadoQueElModeloResponde(string asunto) => Modelo.Responder(Json(asunto, IntroduccionValida));

    /// <summary>La API de aceptación corre con <c>IA:TiempoRedaccionSegundos = 1</c> (<see cref="clsPasosCosteo"/>): el modelo tarda 2 s.</summary>
    [Given("que el modelo tarda más que el tiempo máximo de redacción")]
    public void DadoQueElModeloTarda() =>
        Modelo.Demorar(TimeSpan.FromSeconds(clsPasosCosteo.TiempoRedaccionSegundos + 1)).Responder(Json("Cotización tardía", IntroduccionValida));

    [Given("que el modelo responde una introducción con el teléfono {string}")]
    public void DadoUnaIntroduccionConTelefono(string telefono) => Modelo.Responder(Json("Cotización de transporte", $"Comuníquese al {telefono}."));

    [Given("que el modelo responde una introducción que promete {string}")]
    public void DadoUnaIntroduccionConCondicion(string condicion) => Modelo.Responder(Json("Cotización de transporte", $"Le ofrecemos {condicion}."));

    [Given("que el modelo falla con un error {int}")]
    public void DadoQueElModeloFalla(int estado) =>
        Modelo.Fallar(new HttpRequestException($"El proveedor respondió {estado}.", null, (HttpStatusCode)estado));

    [Given("que el modelo responde una introducción que menciona {string}")]
    public void DadoUnaIntroduccionConMonto(string texto) => Modelo.Responder(Json("Cotización de transporte", $"Nuestra propuesta considera {texto}."));

    [Given("que el modelo responde una introducción con la URL {string}")]
    public void DadoUnaIntroduccionConUrl(string url) => Modelo.Responder(Json("Cotización de transporte", $"Conozca más en {url} sobre nosotros."));

    [Given("que el modelo responde una introducción con el correo {string}")]
    public void DadoUnaIntroduccionConCorreo(string correo) => Modelo.Responder(Json("Cotización de transporte", $"Escríbanos a {correo}."));

    [Given("que el modelo responde un asunto de {int} caracteres")]
    public void DadoUnAsuntoLargo(int caracteres) => Modelo.Responder(Json(new string('a', caracteres), IntroduccionValida));

    [Given("que el modelo responde una introducción de {int} caracteres")]
    public void DadoUnaIntroduccionLarga(int caracteres) => Modelo.Responder(Json("Cotización de transporte", new string('b', caracteres)));

    [Given("que el modelo responde un JSON que no cumple el esquema del redactor")]
    public void DadoUnJsonInvalido() => Modelo.Responder("""{"asunto":"Cotización","introduccion":"Hola","precio":3000000}""");

    [Given("{string} pidió redactar los textos de {string}")]
    public async Task DadoQuePidioRedactar(string alias, string cotizacion)
    {
        await CuandoPideRedactar(alias, cotizacion);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
    }

    [When("{string} pide redactar los textos de {string}")]
    public async Task CuandoPideRedactar(string alias, string cotizacion) =>
        await web.EnviarAsync(HttpMethod.Post, $"{Ruta(cotizacion)}/textos/redactar", web.Sesion(alias).Access, ifMatch: await VersionVigenteAsync(cotizacion));

    [When("{string} pide redactar los textos de {string} con sobrescribir")]
    public async Task CuandoPideRedactarConSobrescribir(string alias, string cotizacion) =>
        await web.EnviarAsync(HttpMethod.Post, $"{Ruta(cotizacion)}/textos/redactar?sobrescribir=true", web.Sesion(alias).Access,
            ifMatch: await VersionVigenteAsync(cotizacion));

    [When("{string} guarda en {string} el asunto {string} y la introducción {string}")]
    public async Task CuandoGuarda(string alias, string cotizacion, string asunto, string introduccion) =>
        await GuardarAsync(alias, cotizacion, asunto, introduccion, await VersionVigenteAsync(cotizacion));

    [Given("{string} guardó en {string} el asunto {string} y una introducción válida")]
    [Given("que {string} guardó en {string} el asunto {string} y una introducción válida")]
    public async Task DadoQueGuardo(string alias, string cotizacion, string asunto)
    {
        await CuandoGuardaConIntroduccionValida(alias, cotizacion, asunto);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
    }

    [When("{string} guarda en {string} el asunto {string} y una introducción válida")]
    public async Task CuandoGuardaConIntroduccionValida(string alias, string cotizacion, string asunto) =>
        await GuardarAsync(alias, cotizacion, asunto, IntroduccionValida, await VersionVigenteAsync(cotizacion));

    [When("{string} guarda en {string} el asunto {string} y una introducción válida sin If-Match")]
    public Task CuandoGuardaSinIfMatch(string alias, string cotizacion, string asunto) =>
        GuardarAsync(alias, cotizacion, asunto, IntroduccionValida, null);

    [When("{string} guarda en {string} el asunto {string} y una introducción válida con el ETag guardado")]
    public Task CuandoGuardaConElEtagGuardado(string alias, string cotizacion, string asunto) =>
        GuardarAsync(alias, cotizacion, asunto, IntroduccionValida, margen.EtagGuardado ?? throw new InvalidOperationException("No se guardó ningún ETag."));

    [Then("los textos de {string} tienen la fuente {string} y no están editados")]
    public Task EntoncesSinEditar(string cotizacion, string fuente) => VerificarTextosAsync(cotizacion, fuente, editado: false);

    [Then("los textos de {string} tienen la fuente {string} y están editados")]
    public Task EntoncesEditados(string cotizacion, string fuente) => VerificarTextosAsync(cotizacion, fuente, editado: true);

    [Then("el asunto de {string} es {string}")]
    public async Task EntoncesElAsunto(string cotizacion, string asunto) =>
        (await TextosAsync(cotizacion)).GetProperty("asunto").GetString().Should().Be(asunto);

    [Then("la redacción de {string} queda en la auditoría con el proveedor, el modelo, la duración y sin respaldo")]
    public async Task EntoncesAuditadaSinRespaldo(string cotizacion)
    {
        var salida = await AuditoriaAsync(cotizacion);
        salida.GetProperty("proveedor").GetString().Should().Be(clsChatClientGuionizado.Proveedor);
        salida.GetProperty("modelo").GetString().Should().Be(clsChatClientGuionizado.Modelo);
        salida.GetProperty("duracion_ms").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        salida.GetProperty("respaldo").GetBoolean().Should().BeFalse();
        salida.GetRawText().Should().NotContain("Apreciado", "la auditoría no guarda los textos");
    }

    [Then("la redacción de {string} queda en la auditoría con respaldo")]
    public async Task EntoncesAuditadaConRespaldo(string cotizacion)
    {
        var salida = await AuditoriaAsync(cotizacion);
        salida.GetProperty("respaldo").GetBoolean().Should().BeTrue();
        salida.GetProperty("motivo_respaldo").GetString().Should().Be(clsRedaccionCotizacionService.MotivoTiempoAgotado);
    }

    [Then("el mensaje enviado al modelo menciona el cliente, la línea {string}, {string}, {string} y la moneda {string}")]
    public void EntoncesElMensajeMenciona(string linea, string origen, string destino, string moneda)
    {
        var mensaje = MensajeUsuario();
        mensaje.Should().Contain("CLIENTE 900555111 S.A.S.").And.Contain($"\"{linea}\"").And.Contain(origen).And.Contain(destino)
            .And.Contain($"\"{moneda}\"").And.NotContain("comercial", "el nombre del comercial no va al proveedor (minimización)");
    }

    [Then("el mensaje enviado al modelo no contiene {string}, {string} ni {string}")]
    public void EntoncesElMensajeNoContiene(string uno, string dos, string tres)
    {
        var enviado = string.Join("\n", Modelo.Llamadas.SelectMany(l => l.Mensajes).Select(m => m.Text));
        foreach (var prohibido in new[] { uno, dos, tres })
        {
            enviado.Should().NotContain(prohibido, "la IA nunca recibe costos, margen ni precios (ADR-018 §2a)");
        }
    }

    [Then("el modelo no fue llamado")]
    public void EntoncesElModeloNoFueLlamado() => Modelo.Llamadas.Should().BeEmpty();

    [Then("el PDF de {string} contiene {string}")]
    public async Task EntoncesElPdfContiene(string cotizacion, string texto) =>
        Normalizar(await TextoPdfAsync(cotizacion)).Should().Contain(Normalizar(texto));

    [Then("el PDF de {string} contiene la introducción estándar")]
    public async Task EntoncesElPdfContieneLaIntroduccionEstandar(string cotizacion)
    {
        var texto = Normalizar(await TextoPdfAsync(cotizacion));
        foreach (var parrafo in clsTextosEstandar.TarifaUnica.Intro)
        {
            texto.Should().Contain(Normalizar(parrafo));
        }
    }

    private Task GuardarAsync(string alias, string cotizacion, string asunto, string introduccion, string? ifMatch) =>
        web.EnviarAsync(HttpMethod.Put, $"{Ruta(cotizacion)}/textos", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { asunto, introduccion }), ifMatch);

    private async Task VerificarTextosAsync(string cotizacion, string fuente, bool editado)
    {
        var textos = await TextosAsync(cotizacion);
        textos.GetProperty("fuente").GetString().Should().Be(fuente);
        textos.GetProperty("editado").GetBoolean().Should().Be(editado);
    }

    /// <summary><c>textos</c> del <c>GET .../costeo</c> con la sesión del dueño (la última respuesta sigue siendo la de la acción).</summary>
    private async Task<JsonElement> TextosAsync(string cotizacion)
    {
        using var respuesta = await web.Api.EnviarAsync(HttpMethod.Get, Ruta(cotizacion), Dueno());
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("textos").Clone();
    }

    private async Task<JsonElement> AuditoriaAsync(string cotizacion)
    {
        var salida = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT TOP 1 outputResumen FROM dbo_cotifly.EventosHerramienta "
            + $"WHERE herramienta = '{clsRedaccionCotizacionService.HerramientaAuditoria}' AND input LIKE '%{costeo.Folio(cotizacion)}%' "
            + "AND threadId IS NULL ORDER BY createdAt DESC");
        salida.Should().NotBeNull("la redacción queda en EventosHerramienta sin threadId");
        using var json = JsonDocument.Parse(salida!);
        return json.RootElement.Clone();
    }

    private async Task<string> TextoPdfAsync(string cotizacion)
    {
        using var descarga = await web.Api.EnviarAsync(HttpMethod.Get, $"/pdfs/{costeo.Folio(cotizacion)}.pdf", Dueno());
        descarga.StatusCode.Should().Be(HttpStatusCode.OK);
        return clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync()).Texto;
    }

    private string MensajeUsuario() => Modelo.Llamadas.Should().ContainSingle().Which.Mensajes.Single(m => m.Role == ChatRole.User).Text;

    /// <summary>Token del único comercial del escenario (el dueño de la cotización).</summary>
    private string Dueno() => web.Sesiones.Values.Single().Access;

    /// <summary><c>If-Match</c> con la versión de negocio vigente (<c>versionNegocio</c>, entre comillas).</summary>
    private async Task<string> VersionVigenteAsync(string cotizacion)
    {
        var version = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.Cotizaciones WHERE folio = '{costeo.Folio(cotizacion)}'");
        return $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private string Ruta(string cotizacion) => $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo";

    private static string Json(string asunto, string introduccion) => JsonSerializer.Serialize(new { asunto, introduccion });

    private static string Normalizar(string texto) => Espacios().Replace(texto, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Espacios();
}
