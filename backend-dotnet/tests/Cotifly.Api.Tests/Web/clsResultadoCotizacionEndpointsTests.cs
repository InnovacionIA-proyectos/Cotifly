using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>POST /web/cotizaciones/{folio}/resultado</c> (HU-005.14, ADR-015 §6) por HTTP con SQL Server real y el Nexo falso: dos
/// escrituras (Nexo primero), 409 de Nexo y Nexo caído sin cambio local, 404 para una ajena.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsResultadoCotizacionEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cotizaciones/{folio}/resultado";
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsResultadoCotizacionEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConLaCotizacionEntregadaEnNexoSeRegistraEnNexoYLuegoEnCotifly()
    {
        var (sesion, folio, solicitud) = await EnviarAsync(entregar: true);

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access,
            """{"resultado":"perdida","motivo":"Precio alto","comentario":"Pidió 5 % menos"}""");
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("ganada").GetBoolean().Should().BeFalse();
        json.GetProperty("motivo_rechazo").GetString().Should().Be("Precio alto");
        _nexo.Resultado(solicitud).Should().Be("Perdida");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}' AND motivoRechazo LIKE '%menos%'"))
            .Should().Be("0", "el comentario solo se reenvía a Nexo");
    }

    [Fact]
    public async Task UnBorradorEnNexoEs409CotizacionNoEntregadaSinCambioLocal()
    {
        var (sesion, folio, _) = await EnviarAsync(entregar: false);
        var antes = _nexo.Peticiones.Count;

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"ganada"}""");

        respuesta.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(respuesta, "COTIZACION_NO_ENTREGADA");
        _nexo.Peticiones.Count.Should().BeGreaterThan(antes, "en autocosteo se le pregunta a Nexo (pudo entregarse sin que el polling lo viera)");
        (await EscalarAsync($"SELECT COALESCE(CAST(ganada AS nvarchar(5)), 'null') FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'")).Should().Be("null");
    }

    [Fact]
    public async Task UnaDescartadaSeMarcaConElSeguimientoYNoConElResultado()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.Descartar(_nexo.SolicitudesCreadas[^1]);
        await _api.ProcesarPollingAsync(folio);

        using var resultado = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"perdida"}""");
        using var seguimiento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/seguimiento", sesion.Access, """{"ganada":false,"motivo_rechazo":"Descartada"}""");

        resultado.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(resultado, "COSTEO_NO_LISTO");
        (await VerificarContratoAsync(seguimiento, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.OK)).GetProperty("ganada").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CambiarUnResultadoYaDefinidoEs409SinCambioLocal()
    {
        var (sesion, folio, _) = await EnviarAsync(entregar: true);
        using var primera = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"ganada"}""");
        primera.StatusCode.Should().Be(HttpStatusCode.OK);

        using var segunda = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"perdida","motivo":"x"}""");

        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(segunda, "RESULTADO_YA_DEFINIDO");
        (await EscalarAsync($"SELECT CONCAT(CAST(ganada AS nvarchar(5)), '|', COALESCE(motivoRechazo, '-')) FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'"))
            .Should().Be("1|-");
    }

    [Fact]
    public async Task ConNexoCaidoEs503SinCambioLocalYUnaAjenaEs404()
    {
        var (sesion, folio, _) = await EnviarAsync(entregar: true);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var caido = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"ganada"}""");
        _nexo.Caido = null;
        using var ajena = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", luis.Access, """{"resultado":"ganada"}""");

        caido.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(caido, "COSTEO_NEXO_NO_DISPONIBLE");
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ajena.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Cotización no encontrada o no es tuya."}""");
        (await EscalarAsync($"SELECT COALESCE(CAST(ganada AS nvarchar(5)), 'null') FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'")).Should().Be("null");
    }

    [Fact]
    public async Task ElSeguimientoInterinoRespondeUsarResultadoNexoParaUnaCotizacionDeNexo()
    {
        var (sesion, folio, _) = await EnviarAsync(entregar: true);

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/seguimiento", sesion.Access, """{"ganada":true}""");

        respuesta.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(respuesta, "USAR_RESULTADO_NEXO");
        (await EscalarAsync($"SELECT COALESCE(CAST(ganada AS nvarchar(5)), 'null') FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'")).Should().Be("null");
    }

    private async Task<(clsSesionPrueba Sesion, string Folio, string Solicitud)> EnviarAsync(bool entregar)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        var folio = json.GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.Costear(solicitud, 2_550_000m);
        if (entregar)
        {
            _nexo.Entregar(solicitud);
        }

        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio, solicitud);
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
