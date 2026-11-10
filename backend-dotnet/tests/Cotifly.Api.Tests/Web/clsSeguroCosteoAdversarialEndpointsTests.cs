using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-045.2 (#264), validación adversarial de <c>PUT /web/cotizaciones/{folio}/costeo/seguro</c> por HTTP con SQL Server real y el Nexo
/// falso: idempotencia, rutas que no existen, cotizaciones ajenas, cuerpos mal formados, dos PUT con el mismo <c>If-Match</c>, el PDF
/// vigente, la auditoría de los errores y un cambio de la tasa vigente en la base. Complementa <see cref="clsSeguroCosteoEndpointsTests"/>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSeguroCosteoAdversarialEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cotizaciones/{folio}/costeo/seguro";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;
    private readonly clsEntornoApiWeb _entorno;

    public clsSeguroCosteoAdversarialEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MarcarDeNuevoUnaRutaQueYaTieneElSeguroEsIdempotenteYNoSubeLaVersion()
    {
        var (sesion, folio) = await CosteadaAsync();
        using var primero = await PutAsync(sesion, folio, """{"incluye_seguro":true}""", await EtagAsync(sesion, folio));
        primero.StatusCode.Should().Be(HttpStatusCode.OK);

        using var otraVez = await PutAsync(sesion, folio, """{"incluye_seguro":true}""", primero.Headers.ETag?.Tag);

        var cuerpo = await otraVez.Content.ReadAsStringAsync(Ct);
        otraVez.StatusCode.Should().Be(HttpStatusCode.OK, $"el contrato dice que repetir la marca responde igual sin registrar nada; cuerpo: {cuerpo}");
        otraVez.Headers.ETag.Should().Be(primero.Headers.ETag, "nada cambió: la versión no sube");
        using var json = JsonDocument.Parse(cuerpo);
        json.RootElement.GetProperty("precio_final").GetDecimal().Should().Be(3_862_500m);
        (await FilaRutaAsync(folio)).Should().Be("1|0.003450|862500.00");
    }

    [Fact]
    public async Task QuitarElSeguroDeUnaRutaSinSeguroEsIdempotenteYNoSubeLaVersion()
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        using var quitar = await PutAsync(sesion, folio, """{"incluye_seguro":false,"linea_orden":1}""", etag);

        (await VerificarContratoAsync(quitar, Ruta, _put, HttpStatusCode.OK)).GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m);
        quitar.Headers.ETag?.Tag.Should().Be(etag);
        (await FilaRutaAsync(folio)).Should().Be("0||");
    }

    [Theory]
    [InlineData("""{"incluye_seguro":true,"linea_orden":99}""", "RUTA_INEXISTENTE")]
    [InlineData("""{"incluye_seguro":true,"linea_orden":0}""", "RUTA_INEXISTENTE")]
    [InlineData("""{"incluye_seguro":true,"linea_orden":-1}""", "RUTA_INEXISTENTE")]
    [InlineData("""{"incluye_seguro":true,"linea_solicitud_id":"9b2f2a1c-0000-4000-8000-000000000001"}""", "RUTA_INEXISTENTE")]
    public async Task UnaRutaQueNoExisteResponde400YNadaCambia(string cuerpo, string codigo)
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        using var respuesta = await PutAsync(sesion, folio, cuerpo, etag);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest, await respuesta.Content.ReadAsStringAsync(Ct));
        await VerificarErrorNegocioAsync(respuesta, codigo);
        (await FilaRutaAsync(folio)).Should().Be("0||");
        (await EtagAsync(sesion, folio)).Should().Be(etag);
    }

    [Fact]
    public async Task ConUnIfMatchViejoYUnaRutaQueNoExisteGanaElConflicto()
    {
        var (sesion, folio) = await CosteadaAsync();
        var viejo = await EtagAsync(sesion, folio);
        using var primero = await PutAsync(sesion, folio, """{"incluye_seguro":true}""", viejo);
        primero.StatusCode.Should().Be(HttpStatusCode.OK);

        using var respuesta = await PutAsync(sesion, folio, """{"incluye_seguro":false,"linea_orden":99}""", viejo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(respuesta, "CONFLICTO_CONCURRENCIA");
    }

    [Theory]
    [InlineData("""{"incluye_seguro":null}""")]
    [InlineData("""{"incluye_seguro":"si"}""")]
    [InlineData("""{"incluye_seguro":1}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public async Task UnCuerpoMalFormadoEs400NuncaUn500(string cuerpo)
    {
        var (sesion, folio) = await CosteadaAsync();

        using var respuesta = await PutAsync(sesion, folio, cuerpo, await EtagAsync(sesion, folio));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest, await respuesta.Content.ReadAsStringAsync(Ct));
        (await FilaRutaAsync(folio)).Should().Be("0||");
    }

    [Fact]
    public async Task OtroComercialNoPuedeCambiarElSeguroDeUnaCotizacionAjena()
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var gerente = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var ajeno = await PutAsync(luis, folio, """{"incluye_seguro":true}""", etag);
        using var deGerente = await PutAsync(gerente, folio, """{"incluye_seguro":true}""", etag);

        ajeno.StatusCode.Should().Be(HttpStatusCode.NotFound, "el 404 de las ajenas no dice si el folio existe (convención del repo)");
        deGerente.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        (await FilaRutaAsync(folio)).Should().Be("0||", "ni otro comercial ni el gerente cambian el seguro del dueño");
        (await EtagAsync(sesion, folio)).Should().Be(etag);
    }

    [Fact]
    public async Task UnFolioQueNoExisteEs404()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await PutAsync(sesion, "PCN-XX-01012026-999", """{"incluye_seguro":true}""", "\"1\"");

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Contain("no encontrada").And.NotContain("PCN-XX", "no refleja el folio");
    }

    [Fact]
    public async Task DosPutConElMismoIfMatchSoloUnoGana()
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        var a = PutAsync(sesion, folio, """{"incluye_seguro":true}""", etag);
        var b = PutAsync(sesion, folio, """{"incluye_seguro":true}""", etag);
        var respuestas = await Task.WhenAll(a, b);

        try
        {
            var codigos = respuestas.Select(r => r.StatusCode).ToList();
            codigos.Should().ContainSingle(c => c == HttpStatusCode.OK, "el control de versión deja pasar solo uno");
            codigos.Should().ContainSingle(c => c == HttpStatusCode.Conflict);
            (await FilaRutaAsync(folio)).Should().Be("1|0.003450|862500.00");
            (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'cambiar_seguro_costeo' "
                + $"AND input LIKE '%{folio}%'")).Should().Be(2, "los dos intentos quedan auditados");
        }
        finally
        {
            foreach (var r in respuestas)
            {
                r.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConElPdfVigenteSinEnviarLoInvalidaLoArchivaYSubeLaVersion()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        pdf.StatusCode.Should().Be(HttpStatusCode.Created);
        // Como clsMargenCosteoEndpointsTests: PDF anterior a HU-036 (sin finalizar) para tener un PDF vigente sin enviar.
        await _api.DesfinalizarComoAnteriorAHu036Async(folio);

        using var seguro = await PutAsync(sesion, folio, """{"incluye_seguro":true}""", pdf.Headers.ETag?.Tag);

        var json = await VerificarContratoAsync(seguro, Ruta, _put, HttpStatusCode.OK);
        json.GetProperty("pdf_url").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();
        seguro.Headers.ETag.Should().NotBe(pdf.Headers.ETag);
        (await EscalarAsync<string>("SELECT TOP 1 h.motivo FROM dbo_cotifly.CotizacionPdfsHistorial h JOIN dbo_cotifly.CotizacionesSolicitud c "
            + $"ON c.id = h.cotizacionId WHERE c.folio = '{folio}'")).Should().Be("servicios_conexos");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'cambiar_seguro_costeo' AND exito = 1 "
            + $"AND input LIKE '%{folio}%' AND outputResumen LIKE '%pdf_invalidado%true%'")).Should().Be(1);

        (await FilaRutaAsync(folio)).Should().Be("1|0.003450|862500.00");
    }

    [Fact]
    public async Task ElErrorSinValorSeAuditaSinMontosNiTasa()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = clsCosteoEndpointsTests.Formulario().Replace("\"valor_mercancia\":250000000,", string.Empty, StringComparison.Ordinal);
        using var alta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, formulario);
        var folio = (await VerificarContratoAsync(alta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var seguro = await PutAsync(sesion, folio, """{"incluye_seguro":true}""", await EtagAsync(sesion, folio));

        seguro.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var auditoria = await EscalarAsync<string>("SELECT TOP 1 CONCAT(input, '#', outputResumen) FROM dbo_cotifly.EventosHerramienta "
            + $"WHERE herramienta = 'cambiar_seguro_costeo' AND exito = 0 AND input LIKE '%{folio}%'");
        auditoria.Should().NotBeNull().And.Contain("SEGURO_SIN_VALOR_MERCANCIA");
        auditoria.Should().NotContain("0.00345").And.NotContain("0,345");
    }

    [Fact]
    public async Task UnCambioDeLaTasaVigenteNoMueveLaRutaYaCotizadaYLaSiguienteUsaLaNueva()
    {
        var (sesion, folio) = await CosteadaAsync();
        using var antes = await PutAsync(sesion, folio, """{"incluye_seguro":true}""", await EtagAsync(sesion, folio));
        antes.StatusCode.Should().Be(HttpStatusCode.OK);
        var idNueva = Guid.NewGuid();
        var idVigente = await EscalarAsync<Guid>("SELECT id FROM dbo_cotifly.TarifasSeguro WHERE isActive = 1");
        await EjecutarAsync($"""
            UPDATE dbo_cotifly.TarifasSeguro SET isActive = 0, updatedAt = SYSDATETIMEOFFSET() WHERE id = '{idVigente}';
            INSERT INTO dbo_cotifly.TarifasSeguro (id, tasa, vigenteDesde, isActive, updatedAt)
            VALUES ('{idNueva}', 0.004000, SYSDATETIMEOFFSET(), 1, SYSDATETIMEOFFSET());
            """);
        try
        {
            using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
            var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
            json.GetProperty("tasa_seguro_vigente").GetDecimal().Should().Be(0.004m);
            json.GetProperty("rutas")[0].GetProperty("tasa_seguro").GetDecimal().Should().Be(0.003450m, "la ruta conserva la tasa copiada");
            json.GetProperty("precio_final").GetDecimal().Should().Be(3_862_500m);
            (await FilaRutaAsync(folio)).Should().Be("1|0.003450|862500.00");

            var (sesion2, folio2) = await CosteadaAsync();
            using var otra = await PutAsync(sesion2, folio2, """{"incluye_seguro":true}""", await EtagAsync(sesion2, folio2));
            (await VerificarContratoAsync(otra, Ruta, _put, HttpStatusCode.OK)).GetProperty("rutas")[0].GetProperty("valor_seguro").GetDecimal()
                .Should().Be(1_000_000m, "250.000.000 × 0,4 %");
        }
        finally
        {
            await EjecutarAsync($"""
                DELETE FROM dbo_cotifly.TarifasSeguro WHERE id = '{idNueva}';
                UPDATE dbo_cotifly.TarifasSeguro SET isActive = 1 WHERE id = '{idVigente}';
                """);
        }
    }

    private Task<string?> FilaRutaAsync(string folio) => EscalarAsync<string>(
        "SELECT CONCAT(CAST(l.incluyeSeguro AS INT), '|', l.tasaSeguroAplicada, '|', l.costoSeguro) FROM dbo_cotifly.CotizacionLineasSolicitud l "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND l.orden = 1");

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_entorno.BaseDatos.CadenaCotifly, sql);

    private Task EjecutarAsync(string sql) => clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly, sql);

    private async Task<(clsSesionPrueba Sesion, string Folio)> CosteadaAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<HttpResponseMessage> PutAsync(clsSesionPrueba sesion, string folio, string cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(_put, $"/web/cotizaciones/{folio}/costeo/seguro");
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
