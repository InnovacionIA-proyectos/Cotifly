using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-045.2 (#264, ADR-029): <c>PUT /web/cotizaciones/{folio}/costeo/seguro</c> por HTTP con SQL Server real (script 20 aplicado: tasa
/// vigente 0,345 %) y el Nexo falso. El formulario de prueba trae <c>valor_mercancia = 250.000.000</c>: el seguro es 862.500 y se suma al
/// precio sin margen.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSeguroCosteoEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cotizaciones/{folio}/costeo/seguro";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;
    private readonly clsEntornoApiWeb _entorno;

    public clsSeguroCosteoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IncluirElSeguroSumaElValorSinMargenLoGuardaConLaTasaYQuitarloVuelveAlPrecio()
    {
        var (sesion, folio) = await CosteadaAsync();
        using var antes = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var costeo = await VerificarContratoAsync(antes, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        costeo.GetProperty("tasa_seguro_vigente").GetDecimal().Should().Be(0.003450m, "la tasa inicial del script 20, para mostrarla antes de marcar");
        var ruta = costeo.GetProperty("rutas")[0];
        ruta.GetProperty("incluye_seguro").GetBoolean().Should().BeFalse();
        ruta.GetProperty("valor_seguro").ValueKind.Should().Be(JsonValueKind.Null);
        var precio = costeo.GetProperty("precio_final").GetDecimal();
        precio.Should().Be(3_000_000m, "2.550.000 / 0,85");

        using var incluir = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/seguro", sesion.Access,
            """{"incluye_seguro":true,"linea_orden":1}""", antes.Headers.ETag?.Tag);

        var json = await VerificarContratoAsync(incluir, Ruta, _put, HttpStatusCode.OK);
        incluir.Headers.ETag.Should().NotBeNull().And.NotBe(antes.Headers.ETag, "el cambio sube la versión");
        var conSeguro = json.GetProperty("rutas")[0];
        conSeguro.GetProperty("incluye_seguro").GetBoolean().Should().BeTrue();
        conSeguro.GetProperty("tasa_seguro").GetDecimal().Should().Be(0.003450m);
        conSeguro.GetProperty("valor_seguro").GetDecimal().Should().Be(862_500m, "250.000.000 × 0,345 %");
        conSeguro.GetProperty("precio_final_linea").GetDecimal().Should().Be(3_862_500m, "el seguro no lleva margen");
        conSeguro.GetProperty("costo_final").GetDecimal().Should().Be(2_550_000m, "el seguro no es costo de Nexo");
        json.GetProperty("precio_final").GetDecimal().Should().Be(3_862_500m);
        json.GetProperty("tasa_seguro_vigente").GetDecimal().Should().Be(0.003450m);
        (await FilaRutaAsync(folio)).Should().Be("1|0.003450|862500.00", "la base guarda la tasa aplicada y el seguro en pesos");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'cambiar_seguro_costeo' AND exito = 1 "
            + $"AND input LIKE '%{folio}%' AND outputResumen NOT LIKE '%862500%' AND outputResumen NOT LIKE '%0.00345%'")).Should().Be(1, "auditado sin montos");

        using var quitar = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/seguro", sesion.Access,
            """{"incluye_seguro":false}""", incluir.Headers.ETag?.Tag);

        var sinSeguro = await VerificarContratoAsync(quitar, Ruta, _put, HttpStatusCode.OK);
        sinSeguro.GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m);
        sinSeguro.GetProperty("rutas")[0].GetProperty("tasa_seguro").ValueKind.Should().Be(JsonValueKind.Null);
        (await FilaRutaAsync(folio)).Should().Be("0||", "sin seguro, la tasa y el costo quedan en NULL (CHECK del script 20)");
    }

    [Fact]
    public async Task ExigeIfMatchYIncluyeSeguroYDetectaElConflicto()
    {
        var (sesion, folio) = await CosteadaAsync();
        var ruta = $"/web/cotizaciones/{folio}/costeo/seguro";
        var etag = await EtagAsync(sesion, folio);

        using var sinIfMatch = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"incluye_seguro":true}""", null);
        sinIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        await VerificarErrorNegocioAsync(sinIfMatch, "PRECONDICION_REQUERIDA");

        using var sinCampo = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"linea_orden":1}""", etag);
        sinCampo.StatusCode.Should().Be(HttpStatusCode.BadRequest, await sinCampo.Content.ReadAsStringAsync(Ct));

        using var primero = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"incluye_seguro":true}""", etag);
        primero.StatusCode.Should().Be(HttpStatusCode.OK);
        using var viejo = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"incluye_seguro":false}""", etag);
        viejo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(viejo, "CONFLICTO_CONCURRENCIA");
        (await FilaRutaAsync(folio)).Should().StartWith("1|", "el conflicto no cambia nada");
    }

    [Fact]
    public async Task ConLaCotizacionFinalizadaRespondeElMismo409QueElMargenYNadaCambia()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarAsync(sesion.Access, folio);
        var etag = await EtagAsync(sesion, folio);

        using var seguro = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/seguro", sesion.Access, """{"incluye_seguro":true}""", etag);
        using var margen = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access, """{"margen":0.2}""", etag);

        seguro.StatusCode.Should().Be(HttpStatusCode.Conflict);
        margen.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(seguro, "PDF_YA_ENVIADO");
        await VerificarErrorNegocioAsync(margen, "PDF_YA_ENVIADO");
        using var cuerpo = JsonDocument.Parse(await seguro.Content.ReadAsStringAsync(Ct));
        cuerpo.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("accion").GetString().Should().Be("nueva_version");
        (await FilaRutaAsync(folio)).Should().Be("0||");
    }

    [Fact]
    public async Task SinValorDeLaMercanciaRespondeSeguroSinValorMercancia()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = clsCosteoEndpointsTests.Formulario().Replace("\"valor_mercancia\":250000000,", string.Empty, StringComparison.Ordinal);
        formulario.Should().NotContain("valor_mercancia");
        using var alta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, formulario);
        var folio = (await VerificarContratoAsync(alta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var seguro = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/seguro", sesion.Access, """{"incluye_seguro":true}""",
            await EtagAsync(sesion, folio));

        seguro.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(seguro, "SEGURO_SIN_VALOR_MERCANCIA");
    }

    [Fact]
    public async Task LaVersionNuevaConOtroValorDeLaMercanciaRecalculaElSeguro()
    {
        var (sesion, folio) = await CosteadaAsync();
        using var incluir = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/seguro", sesion.Access, """{"incluye_seguro":true}""",
            await EtagAsync(sesion, folio));
        incluir.StatusCode.Should().Be(HttpStatusCode.OK);

        using var peticion = new HttpRequestMessage(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar");
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        peticion.Headers.TryAddWithoutValidation("If-Match", incluir.Headers.ETag?.Tag);
        peticion.Content = new StringContent(
            """{"motivo":"El cliente cambió el valor de la mercancía","nueva_version":true,"datos_carga":{"valor_mercancia":300000000}}""",
            System.Text.Encoding.UTF8, "application/json");
        using var version = await _api.Cliente.SendAsync(peticion, Ct);

        var json = await VerificarContratoAsync(version, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, HttpStatusCode.Created);
        var folioNuevo = json.GetProperty("folio").GetString()!;
        (await FilaRutaAsync(folioNuevo)).Should().Be("1|0.003450|1035000.00", "HU-045: 300.000.000 × 0,345 % (decisión 5)");
        (await FilaRutaAsync(folio)).Should().Be("1|0.003450|862500.00", "la original no cambia");
    }

    /// <summary><c>incluyeSeguro|tasaSeguroAplicada|costoSeguro</c> de la ruta 1 del folio, como los guarda la base.</summary>
    private Task<string?> FilaRutaAsync(string folio) => EscalarAsync<string>(
        "SELECT CONCAT(CAST(l.incluyeSeguro AS INT), '|', l.tasaSeguroAplicada, '|', l.costoSeguro) FROM dbo_cotifly.CotizacionLineasSolicitud l "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND l.orden = 1");

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_entorno.BaseDatos.CadenaCotifly, sql);

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

    private async Task<HttpResponseMessage> EnviarConVersionAsync(HttpMethod metodo, string ruta, string token, string? cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (cuerpo is not null)
        {
            peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        }

        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
