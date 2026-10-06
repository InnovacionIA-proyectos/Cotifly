using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// "Cotizar costo" (HU-013.4, #100, ADR-018 §5, §8 y §11) por HTTP con SQL Server real y el Nexo falso: re-solicitar es una captura
/// nueva en Nexo (Nexo no tiene "reabrir"), en el mismo folio (200) o en la versión <c>-vN</c> (201), con <c>If-Match</c> obligatorio e
/// <c>Idempotency-Key</c> contra el doble clic.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsReSolicitudCosteoEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cotizaciones/{folio}/costeo/re-solicitar";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsBaseDatosPruebas _baseDatos;
    private readonly clsApiWebPruebas _api;

    public clsReSolicitudCosteoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _baseDatos = entorno.BaseDatos;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnCosteoVencidoSeReSolicitaEnElMismoFolioConUnaCapturaNuevaYElPollingSeReanuda()
    {
        var (sesion, folio) = await EnviarAsync();
        var anterior = _nexo.SolicitudesCreadas[^1];
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("costeo_vencido");
        var etag = await EtagAsync(sesion, folio);

        using var respuesta = await ReSolicitarAsync(sesion, folio, """{"motivo":"El costeo venció"}""", etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);
        json.GetProperty("folio").GetString().Should().Be(folio);
        json.GetProperty("version").GetInt32().Should().Be(1);
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        json.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        respuesta.Headers.ETag.Should().NotBeNull();
        respuesta.Headers.ETag!.Tag.Should().NotBe(etag, "la re-solicitud sube la versión de negocio").And.Be(await EtagAsync(sesion, folio));
        _nexo.SolicitudesCreadas.Should().HaveCount(2, "Nexo no tiene 'reabrir': re-solicitar es una captura nueva");
        var nueva = _nexo.SolicitudesCreadas[^1];
        nueva.Should().NotBe(anterior);
        (await ColumnaAsync(folio, "costeoId")).Should().Be(nueva, "se guarda el id de la solicitud nueva");
        (await ColumnaAsync(folio, "costeoSiguienteConsultaAt")).Should().NotBeNull("el polling se reanuda");
        var alta = _nexo.UltimaAlta!;
        alta["clienteBp"]!.GetValue<string>().Should().Be("15927");
        alta["notas"]!.GetValue<string>().Should().EndWith($"Re-solicitud de {folio}; reemplaza la solicitud {anterior}.", "trazabilidad en Nexo");
        alta["lineas"]![0]!["origen"]!.GetValue<string>().Should().Be("Buenaventura");
        alta["lineas"]![0]!["posiciones"]!.AsArray().Should().HaveCount(2);

        (await AuditoriaAsync(folio)).Should().Contain("El costeo venció").And.Contain(sesion.UsuarioId.ToString())
            .And.Contain(anterior, "el costeo anterior queda en la auditoría");

        _nexo.Costear(nueva, 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("autocosteo", "el polling consulta la solicitud nueva");
    }

    [Fact]
    public async Task EntregadaConPdfSinEnviarSeReSolicitaEnElMismoFolioOlvidaElPrecioEInvalidaElPdf()
    {
        var (sesion, folio) = await EntregadaAsync();
        using (var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access))
        {
            pdf.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var respuesta = await ReSolicitarAsync(sesion, folio, """{"motivo":"El cliente pidió actualizar el valor"}""", await EtagAsync(sesion, folio));

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        json.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null, "el precio anterior se limpia");
        json.GetProperty("pdf_url").ValueKind.Should().Be(JsonValueKind.Null);
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var vista = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        vista.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();
        (await ColumnaAsync(folio, "costo")).Should().BeNull("el costo anterior solo queda en la auditoría");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.CotizacionPdfsHistorial h JOIN dbo_cotifly.Cotizaciones c ON c.id = h.cotizacionId "
            + $"WHERE c.folio = '{folio}' AND h.motivo = 're_solicitud'")).Should().Be(1);
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.Cotizaciones c ON c.id = l.cotizacionId "
            + $"WHERE c.folio = '{folio}' AND (l.costo IS NOT NULL OR l.lineaSolicitudNexoId IS NOT NULL)")).Should().Be(0, "la línea olvida el costeo anterior");
    }

    [Fact]
    public async Task ConCambiosDeCargaSeCreaLaVersionNuevaLaOriginalNoCambiaYNexoRecibeElPesoNuevo()
    {
        var (sesion, folio) = await EnviarAsync();
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        var etag = await EtagAsync(sesion, folio);

        using var respuesta = await ReSolicitarAsync(sesion, folio,
            """{"motivo":"El cliente cambió el peso","datos_carga":{"peso_kg":28400,"tipo_vehiculo":"2S3"}}""", etag);

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.Created);
        var folioNuevo = json.GetProperty("folio").GetString()!;
        folioNuevo.Should().Be($"{folio}-v2");
        json.GetProperty("version").GetInt32().Should().Be(2);
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        respuesta.Headers.Location!.ToString().Should().Be($"/web/cotizaciones/{folioNuevo}/costeo");
        respuesta.Headers.ETag.Should().NotBeNull();
        (await ColumnaAsync(folioNuevo, "pesoKg")).Should().StartWith("28400");
        (await ColumnaAsync(folioNuevo, "tipoVehiculo")).Should().Be("2S3");
        (await ColumnaAsync(folioNuevo, "carroceria")).Should().Be("PORTACONTENEDORES", "lo que no cambia se copia de la original");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones p JOIN dbo_cotifly.Cotizaciones c ON c.id = p.cotizacionId WHERE c.folio = '{folioNuevo}'"))
            .Should().Be(2);
        _nexo.UltimaAlta!["lineas"]![0]!["peso"]!.GetValue<decimal>().Should().Be(28400m);
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("costeo_vencido", "la original conserva su costeo");
        (await EtagAsync(sesion, folio)).Should().NotBe(etag, "la versión de la original se reserva: un If-Match viejo ya no crea otra versión");
    }

    [Fact]
    public async Task SiNexoRechazaOSeCaeEnLaReSolicitudLaCotizacionQuedaEnErrorEnvioConElMotivo()
    {
        var (sesion, folio) = await EnviarAsync();
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;

        using var caida = await ReSolicitarAsync(sesion, folio, """{"motivo":"El costeo venció"}""", await EtagAsync(sesion, folio));

        var json = await VerificarContratoAsync(caida, Ruta, _post, HttpStatusCode.OK);
        json.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("motivo_estado_costeo").GetString().Should().Contain("HTTP 503");

        // Desde error_envio la re-solicitud equivale a reintentar; un 400 de Nexo es definitivo.
        _nexo.AltaFalla = HttpStatusCode.BadRequest;
        using var rechazo = await ReSolicitarAsync(sesion, folio, """{"motivo":"Nexo estaba caído"}""", await EtagAsync(sesion, folio));
        var rechazada = await VerificarContratoAsync(rechazo, Ruta, _post, HttpStatusCode.OK);
        rechazada.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        rechazada.GetProperty("motivo_estado_costeo").GetString().Should().StartWith("Rechazo definitivo de Nexo");

        _nexo.AltaFalla = null;
        using var otraVez = await ReSolicitarAsync(sesion, folio, """{"motivo":"Reintentar igual"}""", await EtagAsync(sesion, folio));
        otraVez.StatusCode.Should().Be(HttpStatusCode.Conflict, "reenviar los mismos datos que Nexo rechazó no sirve");
        await VerificarErrorNegocioAsync(otraVez, "ESTADO_NO_PERMITE");
    }

    [Fact]
    public async Task UnDobleClicConLaMismaIdempotencyKeyNoCreaDosVersionesNiEnviaDosVecesANexo()
    {
        var (sesion, folio) = await EnviarAsync();
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        var etag = await EtagAsync(sesion, folio);
        var clave = Guid.NewGuid().ToString();
        const string Cuerpo = """{"motivo":"Cambio de peso","datos_carga":{"peso_kg":28400}}""";
        var altasAntes = _nexo.SolicitudesCreadas.Count;

        var respuestas = await Task.WhenAll(
            ReSolicitarAsync(sesion, folio, Cuerpo, etag, clave),
            ReSolicitarAsync(sesion, folio, Cuerpo, etag, clave));

        var cuerpos = new List<string>();
        foreach (var respuesta in respuestas)
        {
            using (respuesta)
            {
                respuesta.StatusCode.Should().Be(HttpStatusCode.Created);
                cuerpos.Add(await respuesta.Content.ReadAsStringAsync(Ct));
            }
        }

        cuerpos[1].Should().Be(cuerpos[0], "la repetición devuelve la respuesta de la primera");
        _nexo.SolicitudesCreadas.Should().HaveCount(altasAntes + 1, "Nexo recibe una sola captura");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}' OR folio LIKE '{folio}-v%'")).Should().Be(2);

        using var otroCuerpo = await ReSolicitarAsync(sesion, folio, """{"motivo":"Otro","datos_carga":{"peso_kg":28450}}""", etag, clave);
        otroCuerpo.StatusCode.Should().Be(HttpStatusCode.BadRequest, "la misma clave con otro cuerpo es un error del cliente");
        (await otroCuerpo.Content.ReadAsStringAsync(Ct)).Should().Contain("Idempotency-Key");
    }

    [Fact]
    public async Task VariasPeticionesSimultaneasSinClaveConElMismoIfMatchCreanUnaSolaVersion()
    {
        var (sesion, folio) = await EnviarAsync();
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        var etag = await EtagAsync(sesion, folio);
        var altas = _nexo.SolicitudesCreadas.Count;

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            ReSolicitarAsync(sesion, folio, """{"motivo":"Doble clic","nueva_version":true}""", etag)));

        var estados = respuestas.Select(r => r.StatusCode).ToList();
        foreach (var respuesta in respuestas)
        {
            respuesta.Dispose();
        }

        estados.Count(e => e == HttpStatusCode.Created).Should().Be(1, string.Join(",", estados));
        estados.Count(e => e == HttpStatusCode.Conflict).Should().Be(3, "las demás pierden la reserva de la versión de la original");
        _nexo.SolicitudesCreadas.Should().HaveCount(altas + 1, "un solo POST a Nexo");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE folio LIKE '{folio}-v%'")).Should().Be(1);
    }

    [Fact]
    public async Task SinIfMatchEs428YConUnoViejoEs409SinLlamarANexo()
    {
        var (sesion, folio) = await EnviarAsync();
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        var altas = _nexo.SolicitudesCreadas.Count;

        using var sinIfMatch = await ReSolicitarAsync(sesion, folio, """{"motivo":"El costeo venció"}""", null);
        using var viejo = await ReSolicitarAsync(sesion, folio, """{"motivo":"El costeo venció"}""", "\"0\"");

        sinIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        await VerificarErrorNegocioAsync(sinIfMatch, "PRECONDICION_REQUERIDA");
        viejo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(viejo, "CONFLICTO_CONCURRENCIA");
        _nexo.SolicitudesCreadas.Should().HaveCount(altas);
    }

    [Fact]
    public async Task EnOtrosEstadosNoSePermiteYAjenaEs404()
    {
        var (sesion, folio) = await EnviarAsync();
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var etag = await EtagAsync(sesion, folio);
        var altas = _nexo.SolicitudesCreadas.Count;

        using var enCurso = await ReSolicitarAsync(sesion, folio, """{"motivo":"Probar"}""", etag);
        using var versionEnCurso = await ReSolicitarAsync(sesion, folio, """{"motivo":"Probar","nueva_version":true}""", etag);
        using var ajena = await ReSolicitarAsync(luis, folio, """{"motivo":"No es mía"}""", etag);

        enCurso.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(enCurso, "ESTADO_NO_PERMITE");
        versionEnCurso.StatusCode.Should().Be(HttpStatusCode.Conflict, "con un costeo en curso tampoco se crea versión");
        await VerificarErrorNegocioAsync(versionEnCurso, "ESTADO_NO_PERMITE");
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _nexo.SolicitudesCreadas.Should().HaveCount(altas);
    }

    [Fact]
    public async Task YaEnviadaElMismoFolioEsPdfYaEnviadoYNuevaVersionCreaLaVersionSinCambiosDeCarga()
    {
        var (sesion, folio) = await EntregadaAsync();
        await clsBaseDatosPruebas.EjecutarAsync(_baseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET estadoEnvio = 'enviada' WHERE folio = '{folio}'");
        var etag = await EtagAsync(sesion, folio);

        using var mismo = await ReSolicitarAsync(sesion, folio, """{"motivo":"Actualizar"}""", etag);
        mismo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(mismo, "PDF_YA_ENVIADO");
        using (var documento = JsonDocument.Parse(await mismo.Content.ReadAsStringAsync(Ct)))
        {
            var detalle = documento.RootElement.GetProperty("error").GetProperty("detalle");
            detalle.GetProperty("endpoint").GetString().Should().Be($"/web/cotizaciones/{folio}/costeo/re-solicitar");
            detalle.GetProperty("cuerpo").GetProperty("nueva_version").GetBoolean().Should().BeTrue();
        }

        using var version = await ReSolicitarAsync(sesion, folio, """{"motivo":"Ajustar después del envío","nueva_version":true}""", etag);
        var json = await VerificarContratoAsync(version, Ruta, _post, HttpStatusCode.Created);
        json.GetProperty("folio").GetString().Should().Be($"{folio}-v2");
        json.GetProperty("estado_envio").GetString().Should().Be("sin_enviar");
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("entregada");
        (await ColumnaAsync(folio, "estadoEnvio")).Should().Be("enviada");
    }

    [Theory]
    [InlineData("""{"motivo":"  "}""")]
    [InlineData("""{"motivo":"Cambio","datos_carga":{}}""")]
    [InlineData("""{"motivo":"Cambio","datos_carga":{"origen":"Cali"}}""")]
    [InlineData("""{"motivo":"Cambio","datos_carga":{"peso_kg":"28000"}}""")]
    [InlineData("""{"motivo":"Cambio de peso","datos_carga":{"peso_kg":0}}""")]
    public async Task UnaEntradaInvalidaEs400SinCrearVersionNiLlamarANexo(string cuerpo)
    {
        var (sesion, folio) = await EnviarAsync();
        await _api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        var altas = _nexo.SolicitudesCreadas.Count;

        using var respuesta = await ReSolicitarAsync(sesion, folio, cuerpo, await EtagAsync(sesion, folio));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "SOLICITUD_COSTEO_INVALIDA");
        _nexo.SolicitudesCreadas.Should().HaveCount(altas);
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE folio LIKE '{folio}-v%'")).Should().Be(0);
    }

    /// <summary>Cotización enviada, costeada (2.550.000) y entregada por Nexo.</summary>
    private async Task<(clsSesionPrueba Sesion, string Folio)> EntregadaAsync()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        _nexo.Entregar(_nexo.SolicitudesCreadas[^1]);
        await _api.ProcesarPollingAsync(folio);
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("entregada");
        return (sesion, folio);
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<HttpResponseMessage> ReSolicitarAsync(clsSesionPrueba sesion, string folio, string cuerpo, string? ifMatch, string? clave = null)
    {
        using var peticion = new HttpRequestMessage(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar");
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

    private Task<string?> ColumnaAsync(string folio, string columna) =>
        EscalarAsync<string>($"SELECT CAST({columna} AS nvarchar(max)) FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'");

    private Task<string?> AuditoriaAsync(string folio) =>
        EscalarAsync<string>("SELECT STRING_AGG(CONCAT(input, '|', outputResumen), ';') FROM dbo_cotifly.EventosHerramienta "
            + $"WHERE herramienta = 'costeo_nexo_re_solicitud' AND exito = 1 AND input LIKE '%{folio}%'");

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_baseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
