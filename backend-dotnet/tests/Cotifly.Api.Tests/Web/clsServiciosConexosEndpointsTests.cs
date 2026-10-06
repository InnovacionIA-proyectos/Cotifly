using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Servicios conexos del catálogo de Nexo (HU-013.3, ADR-018 §4 y §8) por HTTP con SQL Server real y el Nexo falso: el catálogo sin
/// costos, agregar un conexo en Nexo con el <c>lineaId</c> de su cotización, el precio con el margen, el <c>ETag</c>, el
/// <c>If-Match</c> obligatorio, el <c>Idempotency-Key</c> y los errores de Nexo (400, 404, 403, caída y relectura fallida).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsServiciosConexosEndpointsTests : IDisposable
{
    private const string Almacenamiento = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d";
    private const string Precinto = "7a2d3b0f-4c8e-4d66-8b1f-2e3a4b5c6d7e";
    private const string Escolta = "8b3e4c1a-5d9f-4e77-9c2a-3f4b5c6d7e8f";
    private const string RutaContrato = "/web/cotizaciones/{folio}/costeo/servicios-conexos";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", 120_000m)
        .ConServicioConexo(Precinto, "PRE-01", "Precinto", "adicional", "Unidad", 35_000m)
        .ConServicioConexo(Escolta, "ESC-01", "Escolta", "logistico", "Viaje", 900_000m, activo: false);
    private readonly clsApiWebPruebas _api;

    public clsServiciosConexosEndpointsTests(clsEntornoApiWeb entorno) => _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElCatalogoDevuelveSoloActivosSinElCostoDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(_get, "/web/servicios-conexos?q=o&page=1&page_size=10", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, "/web/servicios-conexos", _get, HttpStatusCode.OK);
        json.GetProperty("total").GetInt32().Should().Be(2);
        json.GetProperty("page_size").GetInt32().Should().Be(10);
        json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nombre").GetString()).Should().BeEquivalentTo("Almacenamiento", "Precinto");
        var cuerpo = json.GetRawText();
        cuerpo.Should().NotContain("costo").And.NotContain("120000", "Cotifly nunca expone el costo de Nexo");
        var consulta = _nexo.Peticiones.Last(p => p.Url.AbsolutePath == "/api/servicios-conexos");
        consulta.Url.Query.Should().Contain("soloActivos=true");
        consulta.IdPortalUsuario.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ElCatalogoTraduceElPermisoYLaCaidaDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        _nexo.CatalogoFalla = HttpStatusCode.Forbidden;
        using var sinPermiso = await _api.EnviarAsync(_get, "/web/servicios-conexos", sesion.Access);
        _nexo.CatalogoFalla = null;
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;
        using var caido = await _api.EnviarAsync(_get, "/web/servicios-conexos", sesion.Access);
        _nexo.Caido = null;
        using var invalida = await _api.EnviarAsync(_get, "/web/servicios-conexos?page_size=51", sesion.Access);

        sinPermiso.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await VerificarErrorNegocioAsync(sinPermiso, "NEXO_SIN_PERMISO");
        caido.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(caido, "NEXO_NO_DISPONIBLE");
        await VerificarContratoAsync(invalida, "/web/servicios-conexos", _get, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AgregarUnConexoLoCreaEnNexoConElLineaIdDeSuCotizacionYLoDejaIncluidoConPrecio()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        using var respuesta = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 2), etag);

        var json = await VerificarContratoAsync(respuesta, RutaContrato, _post, HttpStatusCode.OK);
        var conexo = json.GetProperty("servicios_conexos").EnumerateArray().Single();
        conexo.GetProperty("nombre").GetString().Should().Be("Almacenamiento");
        conexo.GetProperty("precio").GetDecimal().Should().Be(282_353m, "240.000 / (1 - 0,15)");
        conexo.GetProperty("incluido").GetBoolean().Should().BeTrue();
        json.GetRawText().Should().NotContain("240000", "el costo de Nexo no se expone");
        respuesta.Headers.ETag!.Tag.Should().NotBe(etag).And.Be("\"" + json.GetProperty("row_version").GetString() + "\"");

        var cotizacionNexo = _nexo.CotizacionDe(solicitud)!;
        var item = _nexo.PeticionesConexos(cotizacionNexo).Should().ContainSingle().Subject["items"]!.AsArray().Single()!.AsObject();
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, 0));
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Almacenamiento);
        item.ContainsKey("valorAplicado").Should().BeFalse("Nexo calcula el valor: es la fuente de verdad del costo");

        // El polling ve los mismos conexos: no cambia nada ni sube la versión.
        await _api.ProcesarPollingAsync(folio);
        (await EtagAsync(sesion, folio)).Should().Be(respuesta.Headers.ETag.Tag);
    }

    [Fact]
    public async Task SinIfMatchEs428YConUnoViejoEs409SinLlegarANexo()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);

        using var sinIfMatch = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), null);
        using var viejo = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), etag);

        sinIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        await VerificarErrorNegocioAsync(sinIfMatch, "PRECONDICION_REQUERIDA");
        viejo.StatusCode.Should().Be(HttpStatusCode.Conflict, "el PDF cambió la versión");
        await VerificarErrorNegocioAsync(viejo, "CONFLICTO_CONCURRENCIA");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().BeEmpty();
    }

    [Fact]
    public async Task UnDobleClicConLaMismaIdempotencyKeyDevuelveLaMismaRespuestaSinRepetirElPost()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);
        var clave = Guid.NewGuid().ToString();

        var envios = await Task.WhenAll(
            AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), etag, clave),
            AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), etag, clave));
        using var primera = envios[0];
        using var segunda = envios[1];
        using var otroCuerpo = await AgregarAsync(sesion, folio, Cuerpo(1, Precinto, 1), etag, clave);

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        segunda.StatusCode.Should().Be(HttpStatusCode.OK);
        (await segunda.Content.ReadAsStringAsync(Ct)).Should().Be(await primera.Content.ReadAsStringAsync(Ct));
        segunda.Headers.ETag.Should().Be(primera.Headers.ETag);
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("el POST de Nexo no es idempotente");
        otroCuerpo.StatusCode.Should().Be(HttpStatusCode.BadRequest, "la misma clave con otro cuerpo");
    }

    [Fact]
    public async Task VariasPeticionesSimultaneasSinClaveLleganUnaSolaVezANexo()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        var envios = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), etag)));
        try
        {
            envios.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            envios.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
            _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("la versión se reserva en la base antes del POST");
        }
        finally
        {
            foreach (var envio in envios)
            {
                envio.Dispose();
            }
        }
    }

    [Fact]
    public async Task SiLaRespuestaDelPostSeCortaYLaRelecturaConfirmaElConexoSeTerminaNormal()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        _nexo.CortarRespuestaConexos = true;

        using var respuesta = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 2), await EtagAsync(sesion, folio));

        var json = await VerificarContratoAsync(respuesta, RutaContrato, _post, HttpStatusCode.OK);
        json.GetProperty("servicios_conexos").EnumerateArray().Should().ContainSingle(s => s.GetProperty("incluido").GetBoolean());
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("no se reintenta el POST");
    }

    [Fact]
    public async Task SiLaRespuestaSeCortaYNoSePuedeReleerElAgregadoEsDesconocido()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        _nexo.CortarRespuestaConexos = true;
        _nexo.FallarRelecturaTrasConexos = true;

        using var respuesta = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), await EtagAsync(sesion, folio));

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarContratoAsync(respuesta, RutaContrato, _post, HttpStatusCode.ServiceUnavailable);
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        documento.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("agregado_en_nexo").GetString().Should().Be("desconocido");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle();
    }

    [Theory]
    [InlineData(Escolta, "SERVICIO_CONEXO_INACTIVO")]
    [InlineData("9c4f5d2b-6e0a-4f88-8d3b-4a5b6c7d8e9f", "SERVICIO_CONEXO_NO_ENCONTRADO")]
    public async Task UnServicioQueNexoRechazaEsUnConexoInvalidoYNadaCambia(string servicio, string codigoNexo)
    {
        var (sesion, folio, _) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        using var respuesta = await AgregarAsync(sesion, folio, Cuerpo(1, servicio, 1), etag);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        documento.RootElement.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CONEXO_INVALIDO");
        documento.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Be(codigoNexo);
        (await ConexosAsync(sesion, folio)).Should().BeEmpty("nada cambia en Cotifly salvo la versión reservada");
    }

    [Fact]
    public async Task ConNexoCaidoOSinPermisoNadaCambia()
    {
        var (sesion, folio, _) = await CosteadaAsync();

        _nexo.ConexosFalla = HttpStatusCode.Forbidden;
        using var sinPermiso = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), await EtagAsync(sesion, folio));
        _nexo.ConexosFalla = null;
        var etag = await EtagAsync(sesion, folio);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;
        using var caido = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), etag);
        _nexo.Caido = null;

        sinPermiso.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await VerificarErrorNegocioAsync(sinPermiso, "NEXO_SIN_PERMISO");
        using (var documento = JsonDocument.Parse(await sinPermiso.Content.ReadAsStringAsync(Ct)))
        {
            documento.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString()
                .Should().Be("SIN_PERMISO", "solo el código de Nexo, sin su texto");
        }

        caido.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(caido, "NEXO_NO_DISPONIBLE");
        (await EtagAsync(sesion, folio)).Should().Be(etag, "con Nexo caído falla la lectura previa, antes de reservar la versión");
        (await ConexosAsync(sesion, folio)).Should().BeEmpty();
    }

    [Fact]
    public async Task SiLaRelecturaFallaRespondeAgregadoEnNexoYElPollingLoRecoge()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);
        _nexo.FallarRelecturaTrasConexos = true;

        using var respuesta = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 1), etag, Guid.NewGuid().ToString());

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        var error = documento.RootElement.GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
        error.GetProperty("detalle").GetProperty("agregado_en_nexo").GetBoolean().Should().BeTrue();
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("no se reintenta el POST");

        await _api.ProcesarPollingAsync(folio);
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        json.GetProperty("servicios_conexos").EnumerateArray().Should().ContainSingle(s => s.GetProperty("incluido").GetBoolean());
    }

    [Fact]
    public async Task ItemsFueraDelEsquemaYUnaAjenaNoLleganANexo()
    {
        var (sesion, folio, solicitud) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var vacio = await AgregarAsync(sesion, folio, """{"items":[]}""", etag);
        using var cero = await AgregarAsync(sesion, folio, Cuerpo(1, Almacenamiento, 0), etag);
        using var lineaInexistente = await AgregarAsync(sesion, folio, Cuerpo(2, Almacenamiento, 1), etag);
        using var ajena = await AgregarAsync(luis, folio, Cuerpo(1, Almacenamiento, 1), etag);

        await VerificarContratoAsync(vacio, RutaContrato, _post, HttpStatusCode.BadRequest);
        await VerificarContratoAsync(cero, RutaContrato, _post, HttpStatusCode.BadRequest);
        lineaInexistente.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(lineaInexistente, "CONEXO_INVALIDO");
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().BeEmpty();
    }

    /// <summary>Cotización enviada desde el formulario y costeada por Nexo (flete 2.550.000, margen de la política 15 %).</summary>
    private async Task<(clsSesionPrueba Sesion, string Folio, string Solicitud)> CosteadaAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        var folio = json.GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.Costear(solicitud, 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio, solicitud);
    }

    private async Task<List<JsonElement>> ConexosAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        using var documento = JsonDocument.Parse(await costeo.Content.ReadAsStringAsync(Ct));
        return [.. documento.RootElement.GetProperty("servicios_conexos").EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<HttpResponseMessage> AgregarAsync(clsSesionPrueba sesion, string folio, string cuerpo, string? ifMatch, string? clave = null)
    {
        using var peticion = new HttpRequestMessage(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos");
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (clave is not null)
        {
            peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
        }

        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    private static string Cuerpo(int linea, string servicio, decimal cantidad) =>
        clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = linea, servicio_id = servicio, cantidad } } });

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
