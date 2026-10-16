using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// E2E real del 2026-10-16 (observación 1): el polling hizo 69 <c>POST /api/cotizaciones</c> ("crear o recuperar" el borrador) a Nexo QA
/// en unos 20 minutos para 5 solicitudes, una escritura por ciclo. Ahora el polling solo LEE (<c>GET /api/solicitudes/{id}</c> y
/// <c>GET /api/cotizaciones/por-solicitud/{id}</c>); la cotización de Nexo la crean el autocosteo o la Mesa, y su id queda guardado en
/// <c>cotizacionNexoId</c> la primera vez que aparece. Además, el 409 pasajero de Nexo al coincidir dos lecturas se reintenta una vez.
/// Por HTTP con SQL Server real y el Nexo falso, contando las llamadas.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPollingSinEscriturasNexoTests : IDisposable
{
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsPollingSinEscriturasNexoTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElPollingNoEscribeEnNexoYGuardaElIdDeLaCotizacionCuandoAparece()
    {
        var (folio, solicitud) = await CapturarAsync();
        var escriturasTrasLaCaptura = _nexo.EscriturasCotizaciones;

        // Cinco ciclos sin costeo: Nexo todavía no tiene cotización (204). Antes, cinco "crear o recuperar".
        for (var ciclo = 0; ciclo < 5; ciclo++)
        {
            (await _api.ProcesarPollingAsync(folio)).Should().Be(1);
        }

        _nexo.EscriturasCotizaciones.Should().Be(escriturasTrasLaCaptura, "el polling no crea borradores en Nexo");
        _nexo.Llamadas(HttpMethod.Post, "/api/cotizaciones").Should().Be(0);
        _nexo.Llamadas(HttpMethod.Get, $"/api/cotizaciones/por-solicitud/{solicitud}").Should().Be(5, "una lectura por ciclo");
        _nexo.CotizacionDe(solicitud).Should().BeNull("Cotifly no creó la cotización en Nexo");
        (await ColumnaAsync(folio, "cotizacionNexoId")).Should().BeNull();
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("pendiente_costeo");

        // La Mesa costea (crea la cotización en Nexo): el siguiente ciclo la lee y guarda su id, sin escribir.
        _nexo.Costear(solicitud, 1_700_000m);
        await _api.ProcesarPollingAsync(folio);

        var cotizacionNexo = _nexo.CotizacionDe(solicitud)!;
        (await ColumnaAsync(folio, "cotizacionNexoId")).Should().Be(cotizacionNexo);
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("autocosteo");

        // Con el id guardado, los ciclos siguientes leen la cotización por su id (sin buscarla otra vez).
        var porSolicitud = _nexo.Llamadas(HttpMethod.Get, $"/api/cotizaciones/por-solicitud/{solicitud}");
        await _api.ProcesarPollingAsync(folio);
        _nexo.Llamadas(HttpMethod.Get, $"/api/cotizaciones/{cotizacionNexo}").Should().Be(1);
        _nexo.Llamadas(HttpMethod.Get, $"/api/cotizaciones/por-solicitud/{solicitud}").Should().Be(porSolicitud);
        _nexo.Llamadas(HttpMethod.Post, "/api/cotizaciones").Should().Be(0);
        _nexo.EscriturasCotizaciones.Should().Be(escriturasTrasLaCaptura);
    }

    [Fact]
    public async Task Un409PasajeroDeNexoNoCuentaComoFalloDelPolling()
    {
        var (folio, solicitud) = await CapturarAsync();
        _nexo.Costear(solicitud, 1_700_000m);
        _nexo.ConflictosPasajerosSolicitud = 1;

        await _api.ProcesarPollingAsync(folio);

        _nexo.Llamadas(HttpMethod.Get, $"/api/solicitudes/{solicitud}").Should().BeGreaterThanOrEqualTo(2, "el 409 se reintentó");
        (await ColumnaAsync(folio, "estadoCosteo")).Should().Be("autocosteo", "la segunda lectura trajo el costeo");
        (await ColumnaAsync(folio, "costeoConsultasFallidas")).Should().Be("0");
    }

    [Fact]
    public async Task UnNexoSinLaLecturaPorSolicitudCreaORecuperaUnaVezYDespuesLeePorElId()
    {
        _nexo.SinCotizacionPorSolicitud = true;
        var (folio, solicitud) = await CapturarAsync();

        for (var ciclo = 0; ciclo < 3; ciclo++)
        {
            await _api.ProcesarPollingAsync(folio);
        }

        // Compatibilidad con un Nexo anterior a #286: sin la ruta de lectura, el primer ciclo hace UNA VEZ el "crear o recuperar" y guarda
        // el id; los siguientes leen la cotización por su id.
        var cotizacionNexo = _nexo.CotizacionDe(solicitud);
        _nexo.Llamadas(HttpMethod.Post, "/api/cotizaciones").Should().Be(1, "una sola escritura por solicitud");
        (await ColumnaAsync(folio, "cotizacionNexoId")).Should().Be(cotizacionNexo);
        _nexo.Llamadas(HttpMethod.Get, $"/api/cotizaciones/{cotizacionNexo}").Should().Be(2);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }

    private async Task<(string Folio, string Solicitud)> CapturarAsync()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var cuerpo = await envio.Content.ReadAsStringAsync(Ct);
        envio.StatusCode.Should().Be(HttpStatusCode.Created, cuerpo);
        using var json = JsonDocument.Parse(cuerpo);
        return (json.RootElement.GetProperty("folio").GetString()!, _nexo.SolicitudesCreadas[^1]);
    }

    private Task<string?> ColumnaAsync(string folio, string columna) =>
        clsBaseDatosPruebas.EscalarAsync<string?>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT CAST({columna} AS nvarchar(100)) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");
}
