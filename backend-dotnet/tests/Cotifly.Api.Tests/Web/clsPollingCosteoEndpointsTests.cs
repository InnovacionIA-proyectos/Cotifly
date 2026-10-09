using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Polling del costeo (HU-005.6, ADR-015 §4) de punta a punta: envío por HTTP, cambios en el Nexo falso, una vuelta del worker
/// (<c>clsWorkerCosteoNexo.ProcesarLoteAsync</c>) y el estado visible en "Mis cotizaciones", contra SQL Server real.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPollingCosteoEndpointsTests : IDisposable
{
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsPollingCosteoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task CuandoNexoCosteaLaLineaElPollingLaPasaAAutocosteoYLoAudita()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);

        await _api.ProcesarPollingAsync(folio);

        var fila = await FilaAsync(sesion, folio);
        fila.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        (await EscalarAsync($"SELECT CONCAT(cotizacionNexoId, '|', costeoConsultasFallidas, '|', IIF(costeoSiguienteConsultaAt IS NULL, 'sin', 'con')) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be($"{_nexo.CotizacionDe(_nexo.SolicitudesCreadas[^1])}|0|con");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'costeo_nexo_estado' AND exito = 1 AND input LIKE '%{folio}%'"))
            .Should().Be("1");
    }

    [Fact]
    public async Task UnaCotizacionEntregadaEnNexoSaleDeLaAgenda()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        _nexo.Entregar(_nexo.SolicitudesCreadas[^1]);

        await _api.ProcesarPollingAsync(folio);

        (await FilaAsync(sesion, folio)).GetProperty("estado_costeo").GetString().Should().Be("entregada");
        (await EscalarAsync($"SELECT IIF(costeoSiguienteConsultaAt IS NULL, 'sin', 'con') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("sin");
    }

    [Fact]
    public async Task ConNexoCaidoElEstadoNoCambiaElErrorQuedaAuditadoYSeSigueConsultando()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        await _api.ProcesarPollingAsync(folio);

        (await FilaAsync(sesion, folio)).GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        (await EscalarAsync($"SELECT CONCAT(costeoConsultasFallidas, '|', IIF(costeoSiguienteConsultaAt IS NULL, 'sin', 'con')) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be("1|con");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'costeo_nexo_consulta' AND exito = 0 AND input LIKE '%{folio}%'"))
            .Should().Be("1");

        _nexo.Caido = null;
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 1_000_000m);
        await _api.ProcesarPollingAsync(folio);
        (await FilaAsync(sesion, folio)).GetProperty("estado_costeo").GetString().Should().Be("autocosteo", "cuando Nexo vuelve, el polling sigue");
    }

    [Fact]
    public async Task UnIdQueNexoYaNoReconoceDejaDeConsultarseConElMotivoYSeAudita()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Olvidar(_nexo.SolicitudesCreadas[^1]);

        await _api.ProcesarPollingAsync(folio);

        var fila = await FilaAsync(sesion, folio);
        fila.GetProperty("estado_costeo").GetString().Should().Be("costeo_vencido");
        fila.GetProperty("motivo_estado_costeo").GetString().Should().Contain("ya no reconoce");
        (await EscalarAsync($"SELECT IIF(costeoSiguienteConsultaAt IS NULL, 'sin', 'con') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("sin");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'costeo_nexo_consulta' AND outputResumen LIKE '%HTTP 404%' AND input LIKE '%{folio}%'"))
            .Should().Be("1");
    }

    [Fact]
    public async Task UnaSolicitudDescartadaEnNexoQuedaDescartadaYSaleDeLaAgenda()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Descartar(_nexo.SolicitudesCreadas[^1]);

        await _api.ProcesarPollingAsync(folio);

        (await FilaAsync(sesion, folio)).GetProperty("estado_costeo").GetString().Should().Be("descartada");
        (await EscalarAsync($"SELECT IIF(costeoSiguienteConsultaAt IS NULL, 'sin', 'con') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("sin");
    }

    [Fact]
    public async Task SinCosteoPasadaLaEdadMaximaQuedaVencidaYVisibleEnMisCotizaciones()
    {
        var (sesion, folio) = await EnviarAsync();

        await _api.ProcesarPollingAsync(folio, enviadaHace: TimeSpan.FromHours(73));

        var fila = await FilaAsync(sesion, folio);
        fila.GetProperty("estado_costeo").GetString().Should().Be("costeo_vencido");
        fila.GetProperty("motivo_estado_costeo").GetString().Should().Contain("72 h");
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", HttpMethod.Post, HttpStatusCode.Created);
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    private async Task<JsonElement> FilaAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/mis-cotizaciones", HttpMethod.Get, HttpStatusCode.OK);
        return json.GetProperty("cotizaciones").EnumerateArray().Single(c => c.GetProperty("folio").GetString() == folio);
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
