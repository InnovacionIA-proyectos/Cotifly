using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Contrato;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos HTTP de <c>specs/features/aislamiento_comercial.feature</c> y <c>web_cotizaciones.feature</c> (y los comunes que usa
/// <c>admin_lineas_negocio.feature</c>): API real en memoria, sesiones del login SSO y validación contra el contrato.
/// </summary>
[Binding]
public sealed class clsPasosWeb(clsContextoWeb web)
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;

    private string? _ultimoActor;

    [Given("la API web de Cotifly")]
    public void DadoLaApi() => web.Iniciar(correoHabilitado: false);

    [Given("la API web de Cotifly con el envío de correo habilitado")]
    public void DadoLaApiConCorreo() => web.Iniciar(correoHabilitado: true);

    [Given("el comercial {string} con sesión en la regional {string}")]
    public Task DadoElComercial(string alias, string regional) => IniciarSesionAsync(alias, clsBaseDatosPruebas.RolExternoComercial, regional);

    [Given("el gerente comercial {string} con sesión en la regional {string}")]
    public Task DadoElGerente(string alias, string regional) => IniciarSesionAsync(alias, clsBaseDatosPruebas.RolExternoGerente, regional);

    /// <summary>Sesión con el rol externo de director de las pruebas (<see cref="clsBaseDatosPruebas.RolExternoDirector"/>).</summary>
    [Given("el director {string} con sesión en la regional {string}")]
    public Task DadoElDirector(string alias, string regional) => IniciarSesionAsync(alias, clsBaseDatosPruebas.RolExternoDirector, regional);

    [Given("que {string} tiene la cotización web {string} del cliente {string} con su PDF")]
    public async Task DadoLaCotizacion(string alias, string cotizacion, string nit) =>
        web.Cotizaciones[cotizacion] = await web.Api.CrearCotizacionAsync(web.Sesion(alias).UsuarioId, nit, creadaEn: web.SiguienteHoraCreacion());

    [Given("que {string} ya envió la cotización {string} a {string}")]
    public Task DadoQueYaEnvio(string alias, string cotizacion, string para) =>
        web.Api.RegistrarEnvioAsync(web.Cotizacion(cotizacion).Id, web.Sesion(alias).UsuarioId, para);

    [When("{string} consulta sus cotizaciones por la API")]
    public Task CuandoConsultaSusCotizaciones(string alias) => web.EnviarAsync(_get, "/web/mis-cotizaciones", web.Sesion(alias).Access);

    [When(@"^""([^""]+)"" intenta (marcar el seguimiento de|enviar por correo|previsualizar el correo de|pedir una versión nueva de|descargar el PDF de) la cotización ""([^""]+)""$")]
    public Task CuandoIntenta(string alias, string operacion, string cotizacion)
    {
        _ultimoActor = alias;
        return OperarAsync(operacion, web.Sesion(alias).Access, web.Cotizacion(cotizacion));
    }

    [Then(@"^intentar (marcar el seguimiento de|enviar por correo|previsualizar el correo de|pedir una versión nueva de|descargar el PDF de) una cotización inexistente da exactamente la misma respuesta$")]
    public async Task EntoncesLaMismaRespuesta(string operacion)
    {
        var (estado, cuerpo) = (web.UltimoEstado, web.UltimoCuerpo);
        var inexistente = new clsCotizacionPrueba(Guid.NewGuid(), "PCN-XX-01011999-999", "PCN-XX-01011999-999.pdf", null);

        await OperarAsync(operacion, web.Sesion(_ultimoActor!).Access, inexistente);

        web.UltimoEstado.Should().Be(estado);
        web.UltimoCuerpo.Should().Be(cuerpo);
    }

    [Then("la cotización {string} sigue sin enviar y sin seguimiento")]
    public async Task EntoncesSigueIgual(string cotizacion)
    {
        var estado = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(estadoEnvio, '|', COALESCE(CAST(ganada AS nvarchar(5)), 'null')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{web.Cotizacion(cotizacion).Id}'");
        estado.Should().Be("sin_enviar|null");
    }

    [When("{string} consulta por la API los destinatarios previos del cliente {string}")]
    public Task CuandoConsultaDestinatarios(string alias, string nit) =>
        web.EnviarAsync(_get, $"/web/clientes/{nit}/destinatarios-previos", web.Sesion(alias).Access);

    [When("{string} descarga el PDF de la cotización {string}")]
    public Task CuandoDescarga(string alias, string cotizacion) =>
        web.EnviarAsync(_get, $"/pdfs/{web.Cotizacion(cotizacion).ArchivoPdf}", web.Sesion(alias).Access);

    [When("{string} marca la cotización {string} como ganada {string} con el motivo {string}")]
    public Task CuandoMarca(string alias, string cotizacion, string ganada, string motivo) =>
        web.EnviarAsync(_post, $"/web/cotizaciones/{web.Cotizacion(cotizacion).Folio}/seguimiento", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { ganada = bool.Parse(ganada), motivo_rechazo = motivo }));

    [When("{string} marca la cotización {string} como ganada {string} con un motivo de {int} caracteres")]
    public Task CuandoMarcaConMotivoLargo(string alias, string cotizacion, string ganada, int caracteres) =>
        CuandoMarca(alias, cotizacion, ganada, new string('m', caracteres));

    [When("{string} guarda el teléfono {string}")]
    public Task CuandoGuardaTelefono(string alias, string telefono) =>
        web.EnviarAsync(_put, "/web/usuario/telefono", web.Sesion(alias).Access, clsApiWebPruebas.Json(new { telefono }));

    [When("{string} consulta su teléfono")]
    public Task CuandoConsultaTelefono(string alias) => web.EnviarAsync(_get, "/web/usuario/telefono", web.Sesion(alias).Access);

    [When("{string} pide los datos para una versión nueva de {string}")]
    public Task CuandoPideNuevaVersion(string alias, string cotizacion) =>
        web.EnviarAsync(_get, $"/web/cotizaciones/{web.Cotizacion(cotizacion).Id}/nueva-version", web.Sesion(alias).Access);

    [When("{string} previsualiza el correo de {string} sin borrador")]
    public Task CuandoPrevisualiza(string alias, string cotizacion) =>
        web.EnviarAsync(_post, $"/web/cotizaciones/{web.Cotizacion(cotizacion).Folio}/correo/vista-previa", web.Sesion(alias).Access);

    [When("{string} envía por la API la cotización {string} a {string}")]
    public Task CuandoEnvia(string alias, string cotizacion, string para) =>
        web.EnviarAsync(_post, $"/web/cotizaciones/{web.Cotizacion(cotizacion).Folio}/enviar", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { para = new[] { para } }));

    [When("se consulta {string} sin sesión")]
    public Task CuandoSinSesion(string operacion)
    {
        var partes = operacion.Split(' ', 2);
        return web.EnviarAsync(new HttpMethod(partes[0]), partes[1], token: null);
    }

    [Then("la respuesta tiene el estado {int}")]
    public void EntoncesElEstado(int estado) => web.UltimoEstado.Should().Be((HttpStatusCode)estado, web.UltimoCuerpo);

    [Then("la respuesta tiene el estado {int} con el error {string}")]
    public void EntoncesElEstadoConError(int estado, string mensaje)
    {
        EntoncesElEstado(estado);
        var json = web.UltimoJson();
        json.EnumerateObject().Select(p => p.Name).Should().Equal("error");
        json.GetProperty("error").GetString().Should().Be(mensaje);
    }

    [Then("la respuesta tiene el estado {int} con un ErrorWeb")]
    public async Task EntoncesElEstadoConErrorWeb(int estado)
    {
        EntoncesElEstado(estado);
        await CumpleAsync("ErrorWeb");
    }

    [Then("la respuesta tiene el estado {int} y cumple el esquema {string} del contrato")]
    public async Task EntoncesCumpleElEsquema(int estado, string esquema)
    {
        EntoncesElEstado(estado);
        await CumpleAsync(esquema);
    }

    [Then("la respuesta tiene el estado {int} con el código de negocio {string}")]
    public async Task EntoncesElCodigoDeNegocio(int estado, string codigo)
    {
        EntoncesElEstado(estado);
        await CumpleAsync("ErrorNegocio");
        web.UltimoJson().GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
    }

    [Then("la respuesta tiene el estado {int} con un PDF")]
    public void EntoncesUnPdf(int estado)
    {
        EntoncesElEstado(estado);
        web.UltimoTipo.Should().Be("application/pdf");
        web.UltimoContenido.Should().Equal(clsApiWebPruebas.PdfMinimo);
    }

    [Then("el listado incluye la cotización {string}")]
    public void EntoncesElListadoIncluye(string cotizacion) => FoliosDelListado().Should().Contain(web.Cotizacion(cotizacion).Folio);

    [Then("el listado no incluye la cotización {string}")]
    public void EntoncesElListadoNoIncluye(string cotizacion) => FoliosDelListado().Should().NotContain(web.Cotizacion(cotizacion).Folio);

    [Then("el listado empieza por la cotización {string} seguida de {string}")]
    public void EntoncesElOrden(string primera, string segunda) =>
        FoliosDelListado().Should().Equal(web.Cotizacion(primera).Folio, web.Cotizacion(segunda).Folio);

    [Then("la cotización {string} del listado trae la línea {string}, el estado de envío {string} y su pdf_url")]
    public void EntoncesLaFila(string cotizacion, string linea, string estado)
    {
        var esperada = web.Cotizacion(cotizacion);
        var fila = web.UltimoJson().GetProperty("cotizaciones").EnumerateArray()
            .Single(c => c.GetProperty("folio").GetString() == esperada.Folio);
        fila.GetProperty("linea_negocio").GetString().Should().Be(linea);
        fila.GetProperty("estado_envio").GetString().Should().Be(estado);
        fila.GetProperty("pdf_url").GetString().Should().Be($"/pdfs/{esperada.ArchivoPdf}");
    }

    [Then("la respuesta trae ganada {string} y motivo_rechazo {string}")]
    public void EntoncesGanadaYMotivo(string ganada, string motivo)
    {
        var json = web.UltimoJson();
        json.GetProperty("ganada").GetBoolean().Should().Be(bool.Parse(ganada));
        json.GetProperty("motivo_rechazo").GetString().Should().Be(motivo);
    }

    [Then("la respuesta trae el teléfono {string}")]
    public void EntoncesElTelefono(string telefono) => web.UltimoJson().GetProperty("telefono").GetString().Should().Be(telefono);

    [Then("los datos traen el folio base de {string} y la versión siguiente {int}")]
    public void EntoncesLosDatos(string cotizacion, int version)
    {
        var json = web.UltimoJson();
        json.GetProperty("folio_base").GetString().Should().Be(web.Cotizacion(cotizacion).Folio);
        json.GetProperty("version_siguiente").GetInt32().Should().Be(version);
    }

    [Then("la vista previa tiene el asunto por defecto de {string} y adjunta su PDF")]
    public void EntoncesLaVistaPrevia(string cotizacion)
    {
        var esperada = web.Cotizacion(cotizacion);
        var json = web.UltimoJson();
        json.GetProperty("asunto").GetString().Should().Be($"Cotización {esperada.Folio} — Botero Soto");
        json.GetProperty("adjunto").GetString().Should().Be(esperada.ArchivoPdf);
    }

    [Then("los destinatarios previos están vacíos")]
    public void EntoncesSinDestinatarios()
    {
        var json = web.UltimoJson();
        json.GetProperty("para").GetArrayLength().Should().Be(0);
        json.GetProperty("cc").GetArrayLength().Should().Be(0);
    }

    private async Task IniciarSesionAsync(string alias, int rolExterno, string regional) =>
        web.Sesiones[alias] = await web.Api.IniciarSesionAsync(rolExterno, web.Regional(regional), $"Persona {alias}");

    private async Task CumpleAsync(string esquema)
    {
        var errores = clsValidadorEsquemaOpenApi.Validar(web.UltimoCuerpo, await clsValidadorEsquemaOpenApi.EsquemaAsync(esquema));
        errores.Should().BeEmpty(web.UltimoCuerpo);
    }

    private List<string?> FoliosDelListado()
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK);
        return [.. web.UltimoJson().GetProperty("cotizaciones").EnumerateArray().Select(c => c.GetProperty("folio").GetString())];
    }

    private Task OperarAsync(string operacion, string token, clsCotizacionPrueba cotizacion) => operacion switch
    {
        "marcar el seguimiento de" => web.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/seguimiento", token, clsApiWebPruebas.Json(new { ganada = true })),
        "enviar por correo" => web.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", token, clsApiWebPruebas.Json(new { para = new[] { "x@empresa.com" } })),
        "previsualizar el correo de" => web.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", token),
        "pedir una versión nueva de" => web.EnviarAsync(_get, $"/web/cotizaciones/{cotizacion.Id}/nueva-version", token),
        "descargar el PDF de" => web.EnviarAsync(_get, $"/pdfs/{cotizacion.ArchivoPdf}", token),
        _ => throw new ArgumentOutOfRangeException(nameof(operacion), operacion, null),
    };
}
