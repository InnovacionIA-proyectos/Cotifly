using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// "Editar solicitud" (ADR-021) por HTTP con SQL Server real y el Nexo falso. <c>GET .../solicitud</c> devuelve el modelo
/// editable con el <c>ETag</c>; el <c>PUT</c> con ese modelo cambiado aplica la edición en sitio, como reemplazo (rechaza la solicitud
/// anterior en Nexo y captura en el mismo folio) o en una versión <c>-vN</c>. Cubre 200, 201, 400, 404, 409, 428 y 503, la idempotencia y
/// la persistencia de las rutas agregadas y quitadas (orden único por solicitud y ajustes de margen sin ruta).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEdicionSolicitudEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cotizaciones/{folio}/solicitud";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private const string Cargue = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d";
    private const string Almacenamiento = "1b2c3d4e-5f60-4a7b-8c9d-0e1f2a3b4c5d";
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m)
        .ConServicioConexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", 120_000m);
    private readonly clsBaseDatosPruebas _baseDatos;
    private readonly clsApiWebPruebas _api;

    public clsEdicionSolicitudEndpointsTests(clsEntornoApiWeb entorno)
    {
        _baseDatos = entorno.BaseDatos;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElGetTraeElModeloEditableConElEtagYUnaAjenaEs404()
    {
        var (sesion, folio) = await EnviarAsync();

        using var respuesta = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/solicitud", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, Ruta, _get, HttpStatusCode.OK);
        respuesta.Headers.ETag.Should().NotBeNull();
        json.GetProperty("bloqueados").GetProperty("nit_cliente").GetString().Should().Be(clsCosteoEndpointsTests.Nit);
        json.GetProperty("edicion").GetProperty("modo").GetString().Should().Be("reemplazo");
        var ruta = json.GetProperty("rutas")[0];
        ruta.GetProperty("linea_negocio").GetString().Should().Be("Importacion");
        ruta.GetProperty("origen_municipio_id").GetInt32().Should().Be(76109);
        ruta.GetProperty("ciudad_devolucion_municipio_id").GetInt32().Should().Be(76109);
        ruta.GetProperty("mercancias")[0].GetProperty("codigo_mercancia").GetString().Should().Be(clsCosteoEndpointsTests.CodigoMercancia);
        json.ToString().Should().NotContain("costo", "el modelo editable no expone costos de Nexo");

        var otro = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var ajena = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/solicitud", otro.Access);
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SinLlegarANexoSeEditaEnSitioYSeEnviaSinRechazarNada()
    {
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        var (sesion, folio) = await EnviarAsync();
        _nexo.AltaFalla = null;
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["destino_municipio_id"] = 5001;
        modelo["rutas"]![0]!["destino"] = "Medellín";

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        json.GetProperty("modo").GetString().Should().Be("en_sitio");
        json.GetProperty("cotizacion").GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        _nexo.IntentosRechazo.Should().Be(0);
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        _nexo.UltimaAlta!["lineas"]![0]!["destino"]!.GetValue<string>().Should().Be("Medellín");
        respuesta.Headers.ETag!.Tag.Should().NotBe(etag);
    }

    [Fact]
    public async Task EnNexoSeRechazaLaAnteriorYSeCapturaEnElMismoFolio()
    {
        var (sesion, folio) = await EnviarAsync();
        var anterior = _nexo.SolicitudesCreadas[^1];
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["horas_cargue"] = 9;
        modelo["motivo"] = "El cliente cambió el horario";

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        (json.GetProperty("modo").GetString(), json.GetProperty("folio").GetString()).Should().Be(("reemplazo", folio));
        json.GetProperty("solicitud_nexo_reemplazada").GetString().Should().Be("descartada");
        json.GetProperty("avisos").EnumerateArray().Select(a => a.GetProperty("codigo").GetString()).Should()
            .Contain(["REEMPLAZA_SOLICITUD_NEXO", "PIERDE_TRABAJO_MESA"]);
        var rechazo = _nexo.Rechazos.Should().ContainSingle().Subject;
        (rechazo.SolicitudId, rechazo.Motivo).Should().Be((anterior, "Edición de la solicitud desde Cotifly"));
        rechazo.Comentario.Should().Contain(folio).And.Contain("El cliente cambió el horario");
        _nexo.EstadoDe(anterior).Should().Be("Descartada");
        _nexo.SolicitudesCreadas.Should().HaveCount(2, "la captura nueva es otra solicitud en Nexo");
        (await ColumnaAsync(folio, "solicitudNexoId")).Should().Be(_nexo.SolicitudesCreadas[^1]);
        _nexo.UltimaAlta!["notas"]!.GetValue<string>().Should().EndWith($"Edición de {folio}; reemplaza la solicitud {anterior}.");
        (await EscalarAsync<string>($"SELECT CAST(l.horasCargue AS nvarchar(20)) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c "
            + $"ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'")).Should().StartWith("9");
    }

    [Fact]
    public async Task SiLaAnteriorYaEstabaDescartadaEnNexoSeContinua()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Descartar(_nexo.SolicitudesCreadas[^1]);
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["valor_mercancia"] = 1000;

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        json.GetProperty("solicitud_nexo_reemplazada").GetString().Should().Be("ya_descartada");
        _nexo.SolicitudesCreadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task SiElRechazoFallaNoSeCapturaOtraSolicitudYRespondeNexoNoDisponible()
    {
        var (sesion, folio) = await EnviarAsync();
        var anterior = _nexo.SolicitudesCreadas[^1];
        _nexo.RechazoFalla = HttpStatusCode.ServiceUnavailable;
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["horas_cargue"] = 9;

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, "NEXO_NO_DISPONIBLE");
        using var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        cuerpo.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Be("RECHAZO_NEXO_FALLIDO");
        _nexo.SolicitudesCreadas.Should().ContainSingle("nunca quedan dos solicitudes vivas en Nexo");
        (await ColumnaAsync(folio, "solicitudNexoId")).Should().Be(anterior);
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("pendiente_costeo");
    }

    /// <summary>
    /// Con el 403 de Nexo al rechazar, el cliente recibe 403 <c>NEXO_SIN_PERMISO</c> con
    /// <c>detalle.motivo = RECHAZO_NEXO_FALLIDO</c>; el texto crudo de Nexo ("HTTP 403 ...") no pisa el motivo.
    /// </summary>
    [Fact]
    public async Task SiNexoNiegaElRechazoEs403ConElMotivoDeCotiflyYSinElTextoDeNexo()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.RechazoFalla = HttpStatusCode.Forbidden;
        _nexo.CodigoRechazoFalla = "FORBIDDEN";
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["horas_cargue"] = 9;

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await VerificarErrorNegocioAsync(respuesta, "NEXO_SIN_PERMISO");
        var texto = await respuesta.Content.ReadAsStringAsync(Ct);
        using var cuerpo = JsonDocument.Parse(texto);
        cuerpo.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Be("RECHAZO_NEXO_FALLIDO");
        texto.Should().NotContain("HTTP 403");
        _nexo.SolicitudesCreadas.Should().ContainSingle();
    }

    [Fact]
    public async Task YaConResultadoSeCreaLaVersionNuevaYLaOriginalNoCambia()
    {
        var (sesion, folio) = await EntregadaAsync();
        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // Como el frontend
        using (var resultado = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access,
                   """{"resultado":"ganada","motivo":"Precio"}"""))
        {
            resultado.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var anterior = await ColumnaAsync(folio, "solicitudNexoId");
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["edicion"]!["modo"]!.GetValue<string>().Should().Be("nueva_version");
        modelo["rutas"]![0]!["horas_cargue"] = 9;
        modelo["modo_esperado"] = "nueva_version";

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.Created);
        json.GetProperty("folio").GetString().Should().Be($"{folio}-v2");
        respuesta.Headers.Location!.ToString().Should().Be($"/web/cotizaciones/{folio}-v2/costeo");
        _nexo.IntentosRechazo.Should().Be(0, "la solicitud de la original es la que tiene el cliente");
        (await ColumnaAsync(folio, "solicitudNexoId")).Should().Be(anterior);
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("entregada");
    }

    [Fact]
    public async Task SeAgreganYQuitanRutasConOrdenUnicoYElAjusteDeLaQuitadaQuedaSinRuta()
    {
        var (sesion, folio) = await EnviarAsync();

        // 1. Se agrega una segunda ruta (Buenaventura → Cali).
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        var nueva = modelo["rutas"]![0]!.DeepClone();
        nueva.AsObject().Remove("linea_solicitud_id");
        nueva["destino_municipio_id"] = 76001;
        nueva["destino"] = "Cali";
        modelo["rutas"]!.AsArray().Add(nueva);
        using (var agregada = await EditarAsync(sesion, folio, modelo, etag))
        {
            var json = await VerificarContratoAsync(agregada, Ruta, _put, HttpStatusCode.OK);
            json.GetProperty("rutas_agregadas").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(2);
        }

        // 2. Nexo costea y el comercial ajusta el margen de la ruta 1 (queda en MargenAjustes con la ruta).
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        using (var margen = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
                   """{"margen":0.3,"linea_orden":1}""", await EtagCosteoAsync(sesion, folio)))
        {
            margen.StatusCode.Should().Be(HttpStatusCode.OK, await margen.Content.ReadAsStringAsync(Ct));
        }

        var ruta1 = await RutaIdAsync(folio, 1);
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE lineaSolicitudId = '{ruta1}'")).Should().Be(1);

        // 3. Se quita la ruta 1, la 2 pasa a ser la 1 y se agrega otra al final (Buenaventura → Yumbo).
        (modelo, etag) = await ModeloAsync(sesion, folio);
        var segunda = modelo["rutas"]![1]!.DeepClone();
        var otra = modelo["rutas"]![1]!.DeepClone();
        otra.AsObject().Remove("linea_solicitud_id");
        otra["destino_municipio_id"] = 76892;
        otra["destino"] = "Yumbo";
        modelo["rutas"] = new JsonArray(segunda, otra);
        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        var resultado = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        resultado.GetProperty("rutas_quitadas")[0].GetString().Should().Be(ruta1);
        (await EscalarAsync<string>($"SELECT STRING_AGG(CONCAT(l.orden, ':', l.destinoMunicipioId), ',') WITHIN GROUP (ORDER BY l.orden) "
            + $"FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("1:76001,2:76892");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes a JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = a.cotizacionId "
            + $"WHERE c.folio = '{folio}' AND a.lineaSolicitudId IS NULL")).Should().Be(1, "el ajuste de la ruta quitada queda en el historial sin ruta");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias m WHERE m.lineaSolicitudId = '{ruta1}'")).Should().Be(0);
    }

    [Fact]
    public async Task ElGetReenviadoComoLoReescribeUnClienteEsSinCambiosYNoTocaNexo()
    {
        // El decimal leído de SQL Server conserva la escala de la columna (28500.50) y el que reenvía un
        // cliente que reescribe el JSON no (28500.5); el deadline puede volver en otra zona y los conexos en otro orden. Es la misma
        // solicitud: 200 sin_cambios, sin rechazar ni capturar en Nexo, sin subir la versión.
        var (sesion, folio) = await EnviarAsync(ConConexos());
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["peso_kg"]!.ToJsonString().Should().Contain(".50", "el GET devuelve la escala de la columna DECIMAL de SQL Server");
        var escrituras = EscriturasNexo();

        using var respuesta = await EditarAsync(sesion, folio, ComoLoReenviaUnCliente(modelo), etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        json.GetProperty("sin_cambios").GetBoolean().Should().BeTrue(json.ToString());
        json.GetProperty("avisos")[0].GetProperty("codigo").GetString().Should().Be("SIN_CAMBIOS");
        _nexo.IntentosRechazo.Should().Be(0, "sin cambios no se rechaza nada en Nexo");
        _nexo.SolicitudesCreadas.Should().ContainSingle("sin cambios no se captura otra solicitud");
        EscriturasNexo().Should().Be(escrituras, "ninguna escritura a Nexo");
        respuesta.Headers.ETag!.Tag.Should().Be(etag, "la versión no cambia");

        // El cuerpo tal cual (con la escala del GET) también es sin cambios.
        using var tal = await EditarAsync(sesion, folio, modelo, etag);
        (await VerificarContratoAsync(tal, Ruta, _put, HttpStatusCode.OK)).GetProperty("sin_cambios").GetBoolean().Should().BeTrue();
        _nexo.IntentosRechazo.Should().Be(0);
        _nexo.SolicitudesCreadas.Should().ContainSingle();
    }

    [Fact]
    public async Task UnKiloMasSiEsUnCambioAunqueElRestoVengaConOtraEscala()
    {
        var (sesion, folio) = await EnviarAsync(ConConexos());
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        var cuerpo = ComoLoReenviaUnCliente(modelo);
        cuerpo["rutas"]![0]!["peso_kg"] = cuerpo["rutas"]![0]!["peso_kg"]!.GetValue<decimal>() + 1m;

        using var respuesta = await EditarAsync(sesion, folio, cuerpo, etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        json.GetProperty("sin_cambios").GetBoolean().Should().BeFalse();
        json.GetProperty("modo").GetString().Should().Be("reemplazo");
        json.GetProperty("rutas_replanificadas").EnumerateArray().Select(e => e.GetInt32()).Should().Equal([1], "el peso viaja al cubicador");
        _nexo.IntentosRechazo.Should().Be(1);
        _nexo.SolicitudesCreadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task CambiarLaCantidadDeUnConexoSiEsUnCambioYNoRecubica()
    {
        var (sesion, folio) = await EnviarAsync(ConConexos());
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        var cuerpo = ComoLoReenviaUnCliente(modelo);
        cuerpo["rutas"]![0]!["servicios_conexos"]![0]!["cantidad"] = 3;

        using var respuesta = await EditarAsync(sesion, folio, cuerpo, etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _put, HttpStatusCode.OK);
        json.GetProperty("sin_cambios").GetBoolean().Should().BeFalse();
        json.GetProperty("rutas_replanificadas").GetArrayLength().Should().Be(0, "un conexo no cambia la carga");
        _nexo.IntentosRechazo.Should().Be(1);
    }

    [Fact]
    public async Task CambiarElClienteEsCampoNoEditable()
    {
        var (sesion, folio) = await EnviarAsync();
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["nit_cliente"] = "800111222";

        using var respuesta = await EditarAsync(sesion, folio, modelo, etag);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CAMPO_NO_EDITABLE");
        using var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        cuerpo.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("campo").GetString().Should().Be("nit_cliente");
        _nexo.IntentosRechazo.Should().Be(0);
    }

    [Fact]
    public async Task SinIfMatchEs428YConUnaVersionViejaEs409()
    {
        var (sesion, folio) = await EnviarAsync();
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["horas_cargue"] = 9;

        using var sinIfMatch = await EditarAsync(sesion, folio, modelo, null);
        sinIfMatch.StatusCode.Should().Be((HttpStatusCode)428);

        using var vieja = await EditarAsync(sesion, folio, modelo, "\"999\"");
        vieja.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(vieja, "CONFLICTO_CONCURRENCIA");
        _nexo.IntentosRechazo.Should().Be(0);
        etag.Should().NotBeNull();
    }

    [Fact]
    public async Task UnaAjenaEs404()
    {
        var (sesion, folio) = await EnviarAsync();
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        var otro = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await EditarAsync(otro, folio, modelo, etag);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConLaMismaIdempotencyKeyLaSegundaDevuelveLaPrimeraSinTocarNexo()
    {
        var (sesion, folio) = await EnviarAsync();
        var (modelo, etag) = await ModeloAsync(sesion, folio);
        modelo["rutas"]![0]!["horas_cargue"] = 9;

        using var primera = await EditarAsync(sesion, folio, modelo, etag, "edicion-12345");
        using var segunda = await EditarAsync(sesion, folio, modelo, etag, "edicion-12345");

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        segunda.StatusCode.Should().Be(HttpStatusCode.OK);
        (await segunda.Content.ReadAsStringAsync(Ct)).Should().Be(await primera.Content.ReadAsStringAsync(Ct));
        _nexo.IntentosRechazo.Should().Be(1);
        _nexo.SolicitudesCreadas.Should().HaveCount(2);
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync(string? formulario = null)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, formulario ?? clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EntregadaAsync()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        _nexo.Entregar(_nexo.SolicitudesCreadas[^1]);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    /// <summary>El modelo del GET (el frontend lo devuelve tal cual en el PUT, con sus cambios) y su ETag.</summary>
    private async Task<(JsonNode Modelo, string? Etag)> ModeloAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/solicitud", sesion.Access);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        return (JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(Ct))!, respuesta.Headers.ETag?.Tag);
    }

    private async Task<string?> EtagCosteoAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<HttpResponseMessage> EditarAsync(clsSesionPrueba sesion, string folio, JsonNode cuerpo, string? ifMatch, string? clave = null)
    {
        using var peticion = new HttpRequestMessage(_put, $"/web/cotizaciones/{folio}/solicitud");
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (clave is not null)
        {
            peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
        }

        peticion.Content = new StringContent(cuerpo.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    /// <summary>El formulario de siempre con dos conexos en la ruta.</summary>
    private static string ConConexos()
    {
        var formulario = JsonNode.Parse(clsCosteoEndpointsTests.Formulario())!;
        formulario["rutas"]![0]!["servicios_conexos"] = new JsonArray(
            new JsonObject { ["id_servicio_conexo"] = Cargue, ["cantidad"] = 2 },
            new JsonObject { ["id_servicio_conexo"] = Almacenamiento, ["cantidad"] = 1.5m });
        return formulario.ToJsonString();
    }

    /// <summary>
    /// El modelo del GET como lo reenvía un cliente que reescribe el JSON (JavaScript, Python): los números sin ceros a la derecha
    /// (<c>28500.50</c> → <c>28500.5</c>), el <c>deadline</c> en otra zona horaria (el mismo instante), los conexos en otro orden y la
    /// moneda en minúsculas.
    /// </summary>
    private static JsonNode ComoLoReenviaUnCliente(JsonNode modelo)
    {
        var copia = modelo.DeepClone();
        SinCeros(copia);
        if (copia["deadline"]?.GetValue<string>() is { } deadline)
        {
            copia["deadline"] = DateTimeOffset.Parse(deadline, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(2)).ToString("o", CultureInfo.InvariantCulture);
        }

        if (copia["moneda"]?.GetValue<string>() is { } moneda)
        {
            copia["moneda"] = moneda.ToLowerInvariant();
        }

        foreach (var ruta in copia["rutas"]!.AsArray())
        {
            if (ruta!["servicios_conexos"] is JsonArray conexos && conexos.Count > 1)
            {
                ruta["servicios_conexos"] = new JsonArray([.. conexos.Reverse().Select(c => c!.DeepClone())]);
            }
        }

        return copia;
    }

    private static void SinCeros(JsonNode? nodo)
    {
        switch (nodo)
        {
            case JsonObject objeto:
                foreach (var (clave, valor) in objeto.ToList())
                {
                    if (valor is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                    {
                        objeto[clave] = JsonValue.Create(decimal.Parse(v.GetValue<decimal>().ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        SinCeros(valor);
                    }
                }

                break;
            case JsonArray arreglo:
                foreach (var elemento in arreglo)
                {
                    SinCeros(elemento);
                }

                break;
        }
    }

    private int EscriturasNexo() => _nexo.Peticiones.Count(p => p.Metodo != _get);

    private Task<string?> RutaIdAsync(string folio, int orden) =>
        EscalarAsync<string>("SELECT LOWER(CAST(l.id AS nvarchar(36))) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c "
            + $"ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND l.orden = {orden}");

    private Task<string?> ColumnaAsync(string folio, string columna) =>
        EscalarAsync<string>($"SELECT CAST(c.{columna} AS nvarchar(max)) FROM dbo_cotifly.CotizacionesSolicitud c WHERE c.folio = '{folio}'");

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_baseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
