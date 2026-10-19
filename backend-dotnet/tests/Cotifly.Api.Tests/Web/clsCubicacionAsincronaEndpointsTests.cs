using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Opción D (D3, E2E 2026-10-16): "Calcular cubicaje" asíncrono por HTTP con SQL Server real y el cubicador falso con una compuerta (el
/// motor "calcula" hasta que la prueba la abre). <c>POST /web/cubicacion</c> responde 200 si el cubicador termina dentro de
/// <c>Cubicador:EsperaSincronaCubicacionSegundos</c>; si no, 202 con <c>Location</c> y <c>Retry-After</c>, y
/// <c>GET /web/cubicacion/{id}</c> muestra <c>en_proceso</c>, <c>completada</c> o <c>fallida</c>. Solo el dueño la ve (404 a otro), el cálculo
/// sigue aunque el cliente corte y, con el cupo global ocupado (opción C), responde 503 <c>CUBICADOR_OCUPADO</c> con <c>Retry-After</c>.
/// Cada respuesta se valida contra <c>specs/openapi.yaml</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCubicacionAsincronaEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cubicacion";
    private const string RutaConsulta = "/web/cubicacion/{id}";
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _get = HttpMethod.Get;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new();
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsCubicacionAsincronaEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("CAFE_EN_ESTIBAS", "Café en estibas"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Cuerpo(int cantidad = 10) => clsApiWebPruebas.Json(new
    {
        items = new[] { new { codigo_mercancia = "CAFE_EN_ESTIBAS", cantidad, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 800 } },
    });

    private clsApiWebPruebas Api(int esperaSincrona = 1, int esperaCupo = 60)
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: _cubicador, extra: new Dictionary<string, string?>
        {
            ["Cubicador:EsperaSincronaCubicacionSegundos"] = esperaSincrona.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cubicador:EsperaMaximaCupoSegundos"] = esperaCupo.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        _apis.Add(api);
        return api;
    }

    [Fact]
    public async Task SiElCubicadorRespondeATiempoEs200ComoSiempreSinLocation()
    {
        var api = Api(esperaSincrona: 5);
        var token = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo());
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("resultado").GetString().Should().Be("ASIGNADO");
        respuesta.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task SiTardaResponde202YElGetPasaDeEnProcesoACompletada()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        var api = Api(esperaSincrona: 1);
        var token = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;

        using var aceptada = await api.EnviarAsync(_post, Ruta, token, Cuerpo());
        var pendiente = await VerificarContratoAsync(aceptada, Ruta, _post, HttpStatusCode.Accepted);

        var id = pendiente.GetProperty("id").GetString()!;
        pendiente.GetProperty("estado").GetString().Should().Be("en_proceso");
        pendiente.GetProperty("consultar_en").GetString().Should().Be($"/web/cubicacion/{id}");
        aceptada.Headers.Location!.OriginalString.Should().Be($"/web/cubicacion/{id}");
        aceptada.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(2));

        using var enProceso = await api.EnviarAsync(_get, $"/web/cubicacion/{id}", token);
        var estado = await VerificarContratoAsync(enProceso, RutaConsulta, _get, HttpStatusCode.OK);
        estado.GetProperty("estado").GetString().Should().Be("en_proceso");
        estado.TryGetProperty("resultado", out _).Should().BeFalse();
        enProceso.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(2));

        compuerta.SetResult();
        var final = await ConsultarHastaTerminarAsync(api, token, id);

        final.GetProperty("estado").GetString().Should().Be("completada");
        final.GetProperty("resultado").GetProperty("resultado").GetString().Should().Be("ASIGNADO");
        final.GetProperty("resultado").GetProperty("vehiculos").GetArrayLength().Should().Be(1);
        final.TryGetProperty("error", out _).Should().BeFalse();
        _cubicador.Recomendaciones.Should().ContainSingle("un solo POST al cubicador");
    }

    [Fact]
    public async Task UnRechazoDelCubicadorEnSegundoPlanoQuedaFallidaConSuCodigo()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, "MERCANCIA_NO_CUBICABLE");
        var api = Api(esperaSincrona: 0);
        var token = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;

        using var aceptada = await api.EnviarAsync(_post, Ruta, token, Cuerpo());
        var id = (await VerificarContratoAsync(aceptada, Ruta, _post, HttpStatusCode.Accepted)).GetProperty("id").GetString()!;
        compuerta.SetResult();
        var final = await ConsultarHastaTerminarAsync(api, token, id);

        final.GetProperty("estado").GetString().Should().Be("fallida");
        final.GetProperty("error").GetProperty("codigo").GetString().Should().Be("MERCANCIA_NO_CUBICABLE");
        final.GetProperty("error").GetProperty("mensaje").GetString().Should().NotBeNullOrWhiteSpace();
        final.TryGetProperty("resultado", out _).Should().BeFalse();
    }

    [Fact]
    public async Task LaCubicacionDeOtroUsuarioOUnaQueNoExisteEs404()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        var api = Api(esperaSincrona: 0);
        var ana = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;
        var luis = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;
        using var aceptada = await api.EnviarAsync(_post, Ruta, ana, Cuerpo());
        var id = (await VerificarContratoAsync(aceptada, Ruta, _post, HttpStatusCode.Accepted)).GetProperty("id").GetString()!;

        using var ajena = await api.EnviarAsync(_get, $"/web/cubicacion/{id}", luis);
        using var inexistente = await api.EnviarAsync(_get, $"/web/cubicacion/{Guid.NewGuid()}", ana);
        using var sinSesion = await api.EnviarAsync(_get, $"/web/cubicacion/{id}", null);

        await VerificarContratoAsync(ajena, RutaConsulta, _get, HttpStatusCode.NotFound);
        await VerificarContratoAsync(inexistente, RutaConsulta, _get, HttpStatusCode.NotFound);
        sinSesion.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ajena.Content.ReadAsStringAsync(Ct)).Should().Be(await inexistente.Content.ReadAsStringAsync(Ct), "no se distingue ajena de inexistente");
        compuerta.SetResult();
        (await ConsultarHastaTerminarAsync(api, ana, id)).GetProperty("estado").GetString().Should().Be("completada", "el dueño sí la ve");
    }

    [Fact]
    public async Task SiElClienteCortaElCalculoSigueYQuedaParaConsultar()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        var api = Api(esperaSincrona: 30);
        var token = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;
        using var corte = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var peticion = new HttpRequestMessage(_post, Ruta) { Content = new StringContent(Cuerpo(), Encoding.UTF8, "application/json") };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var envio = api.Cliente.SendAsync(peticion, corte.Token);
        await EsperarAsync(() => _cubicador.RecomendacionesIniciadas == 1);
        await corte.CancelAsync();
        var cortada = () => envio;
        await cortada.Should().ThrowAsync<OperationCanceledException>();

        compuerta.SetResult();
        await EsperarAsync(() => _cubicador.Corridas.Keys.Any(k => k.StartsWith("PRUEBA-", StringComparison.Ordinal)));
        _cubicador.Recomendaciones.Should().ContainSingle("el POST al cubicador terminó: no lo canceló el corte del cliente");
    }

    [Fact]
    public async Task ConElCupoOcupadoResponde503CubicadorOcupadoConRetryAfter()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        var api = Api(esperaSincrona: 3, esperaCupo: 0);
        var token = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;
        using var primera = await api.EnviarAsync(_post, Ruta, token, Cuerpo());
        await VerificarContratoAsync(primera, Ruta, _post, HttpStatusCode.Accepted);
        await EsperarAsync(() => _cubicador.RecomendacionesIniciadas == 1);

        using var segunda = await api.EnviarAsync(_post, Ruta, token, Cuerpo(cantidad: 20));
        var json = await VerificarContratoAsync(segunda, Ruta, _post, HttpStatusCode.ServiceUnavailable);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CUBICADOR_OCUPADO");
        segunda.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(10));
        _cubicador.RecomendacionesIniciadas.Should().Be(1, "la segunda no llegó al cubicador");
        compuerta.SetResult();
    }

    private static async Task<JsonElement> ConsultarHastaTerminarAsync(clsApiWebPruebas api, string token, string id)
    {
        for (var i = 0; i < 100; i++)
        {
            using var respuesta = await api.EnviarAsync(_get, $"/web/cubicacion/{id}", token);
            var json = await VerificarContratoAsync(respuesta, RutaConsulta, _get, HttpStatusCode.OK);
            if (json.GetProperty("estado").GetString() != "en_proceso")
            {
                return json;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"La cubicación {id} no terminó.");
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        for (var i = 0; i < 200 && !condicion(); i++)
        {
            await Task.Delay(25, Ct);
        }

        condicion().Should().BeTrue();
    }

    public void Dispose()
    {
        _cubicador.CompuertaRecomendacion?.TrySetResult();
        foreach (var api in _apis)
        {
            api.Dispose();
        }

        _nexo.Dispose();
        _cubicador.Dispose();
    }
}
