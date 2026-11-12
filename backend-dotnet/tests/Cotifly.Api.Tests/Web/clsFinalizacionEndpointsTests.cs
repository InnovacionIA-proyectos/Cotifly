using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>POST /web/cotizaciones/{folio}/finalizar</c> por HTTP con SQL Server real (script 14) y el Nexo falso. Cada respuesta
/// se valida contra su esquema de <c>specs/openapi.yaml</c>: 200 <c>CotizacionResumen</c> con <c>finalizada_at</c> y el <c>ETag</c>, 409
/// <c>COTIZACION_NO_FINALIZABLE</c> y <c>CONFLICTO_CONCURRENCIA</c>, 428 sin <c>If-Match</c> y 404 para una ajena.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsFinalizacionEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cotizaciones/{folio}/finalizar";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;
    private readonly clsBaseDatosPruebas _baseDatos;

    public clsFinalizacionEndpointsTests(clsEntornoApiWeb entorno)
    {
        _baseDatos = entorno.BaseDatos;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task FinalizarDevuelveElResumenConFinalizadaAtYElEtagNuevoYReFinalizarNoCambiaNada()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        var etag = await EtagAsync(sesion.Access, folio);

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, null, etag);
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        var finalizadaAt = json.GetProperty("finalizada_at").GetString();
        finalizadaAt.Should().NotBeNullOrEmpty();
        json.GetProperty("estado_costeo").GetString().Should().Be("autocosteo", "la finalización no cambia el estado de costeo");
        respuesta.Headers.ETag!.Tag.Should().NotBe(etag, "finalizar sube la versión de negocio");

        using var otra = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, null, respuesta.Headers.ETag.Tag);
        var repetida = await VerificarContratoAsync(otra, Ruta, _post, HttpStatusCode.OK);
        repetida.GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
        otra.Headers.ETag!.Tag.Should().Be(respuesta.Headers.ETag.Tag, "re-finalizar es idempotente");

        using var lista = await _api.EnviarAsync(_get, "/web/mis-cotizaciones", sesion.Access);
        (await VerificarContratoAsync(lista, "/web/mis-cotizaciones", _get, HttpStatusCode.OK)).GetProperty("cotizaciones").EnumerateArray()
            .Single(c => c.GetProperty("folio").GetString() == folio).GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
        using var detalle = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}", sesion.Access);
        (await VerificarContratoAsync(detalle, "/web/cotizaciones/{folio}", _get, HttpStatusCode.OK))
            .GetProperty("fechas").GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
    }

    [Fact]
    public async Task SinCosteoEsNoFinalizableConElEstadoEnElDetalle()
    {
        var (sesion, folio) = await EnviarAsync();

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, null, await EtagAsync(sesion.Access, folio));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.Conflict);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("COTIZACION_NO_FINALIZABLE");
        json.GetProperty("error").GetProperty("detalle").GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
    }

    [Fact]
    public async Task SinIfMatchEs428YConUnaVersionViejaEsConflicto()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);

        using var sinIfMatch = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access);
        (await VerificarContratoAsync(sinIfMatch, Ruta, _post, HttpStatusCode.PreconditionRequired))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("PRECONDICION_REQUERIDA");

        using var vieja = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, null, "\"0\"");
        (await VerificarContratoAsync(vieja, Ruta, _post, HttpStatusCode.Conflict))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("CONFLICTO_CONCURRENCIA");
        await SinFinalizarAsync(folio);
    }

    [Fact]
    public async Task UnaAjenaEs404()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", luis.Access, null, await EtagAsync(sesion.Access, folio));

        await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.NotFound);
        await SinFinalizarAsync(folio);
    }

    /// <summary>
    /// El director ve cualquier cotización en solo consulta, pero finalizar es solo del
    /// dueño (404, igual que el comercial ajeno) y el costeo del dueño tampoco es suyo; en el detalle ve <c>finalizada_at</c> y el costo
    /// bruto de las rutas le sale en <c>null</c>, como al gerente.
    /// </summary>
    [Fact]
    public async Task ElDirectorNoFinalizaUnaAjenaYEnElDetalleVeFinalizadaAtSinElCostoBruto()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        var director = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoDirector, $"Dir-{Guid.NewGuid():N}"[..16]);

        using var ajena = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", director.Access, null, await EtagAsync(sesion.Access, folio));
        await VerificarContratoAsync(ajena, Ruta, _post, HttpStatusCode.NotFound);
        await SinFinalizarAsync(folio);
        using var costeoAjeno = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", director.Access);
        await VerificarContratoAsync(costeoAjeno, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.NotFound);

        await _api.FinalizarAsync(sesion.Access, folio);
        using var deDueno = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}", sesion.Access);
        using var deDirector = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}", director.Access);
        var dueno = await VerificarContratoAsync(deDueno, "/web/cotizaciones/{folio}", _get, HttpStatusCode.OK);
        var json = await VerificarContratoAsync(deDirector, "/web/cotizaciones/{folio}", _get, HttpStatusCode.OK);

        json.GetProperty("solo_lectura").GetBoolean().Should().BeTrue();
        var finalizadaAt = dueno.GetProperty("fechas").GetProperty("finalizada_at").GetString();
        finalizadaAt.Should().NotBeNullOrEmpty();
        json.GetProperty("fechas").GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
        json.GetProperty("cotizacion").GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
        json.GetProperty("costeo").GetProperty("finalizada_at").GetString().Should().Be(finalizadaAt);
        dueno.GetProperty("costeo").GetProperty("rutas").EnumerateArray()
            .Should().OnlyContain(r => r.GetProperty("costo_final").ValueKind == JsonValueKind.Number, "el dueño sí ve el costo bruto");
        json.GetProperty("costeo").GetProperty("rutas").EnumerateArray()
            .Should().OnlyContain(r => r.GetProperty("costo_final").ValueKind == JsonValueKind.Null, "el costo bruto es solo del dueño");
    }

    private async Task SinFinalizarAsync(string folio) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(_baseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}' AND finalizadaAt IS NULL")).Should().Be(1);

    private async Task<string?> EtagAsync(string token, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", token);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
