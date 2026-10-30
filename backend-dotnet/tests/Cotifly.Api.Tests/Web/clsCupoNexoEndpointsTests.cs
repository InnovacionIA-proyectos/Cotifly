using System.Net;
using System.Text.Json;
using Cotifly.Infraestructura.Comun;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Cupo de lecturas de Nexo de punta a punta (HU-023.4, #159; hueco de la matriz HU-023, #174): por HTTP con SQL Server real y el Nexo
/// falso. Nexo responde 429 a un catálogo con <c>Retry-After</c>; desde ahí Cotifly no vuelve a leer catálogos de Nexo (los bloquea en
/// local) hasta que pase ese tiempo, mientras el polling de la solicitud (<c>GET /api/solicitudes/{id}</c>, sin límite en Nexo) sigue.
/// El reloj del cupo es manual (<see cref="clsTiempoManual"/>) para probar el <c>Retry-After</c> sin esperar.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCupoNexoEndpointsTests : IDisposable
{
    private const int RetryAfter = 45;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsTiempoManual _tiempoCupo = new();
    private readonly clsApiWebPruebas _api;

    public clsCupoNexoEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _entorno = entorno;

        // Sin caché de catálogos (ni copia de respaldo): cada GET /web/catalogos-flete va a Nexo, así se ve si el cupo lo deja salir.
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo,
            extra: new Dictionary<string, string?> { ["Nexo:MinutosCacheCatalogos"] = "0" },
            servicios: s => s.AddSingleton(new clsCupoNexo(_tiempoCupo)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int LecturasDeCatalogos =>
        _nexo.Peticiones.Count(p => p.Metodo == HttpMethod.Get && p.Url.AbsolutePath.StartsWith("/api/catalogos/", StringComparison.Ordinal));

    private int ConsultasDelPolling(string solicitud) =>
        _nexo.Peticiones.Count(p => p.Metodo == HttpMethod.Get && p.Url.AbsolutePath.TrimEnd('/') == $"/api/solicitudes/{solicitud}");

    [Fact]
    public async Task Un429DeUnCatalogoBloqueaSoloLosCatalogosHastaElRetryAfterYElPollingSigue()
    {
        // La solicitud ya está en Nexo antes del 429 (el formulario también lee los catálogos).
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        string folio;
        using (var envio = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario()))
        {
            var cuerpo = await envio.Content.ReadAsStringAsync(Ct);
            envio.StatusCode.Should().Be(HttpStatusCode.Created, cuerpo);
            using var json = JsonDocument.Parse(cuerpo);
            folio = json.RootElement.GetProperty("folio").GetString()!;
        }

        var solicitud = _nexo.SolicitudesCreadas.Should().ContainSingle().Subject;
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.OK);

        // Nexo se queda sin cupo de listados: el próximo GET responde 429 con Retry-After: 45.
        _nexo.LecturasLimitadas = 1;
        _nexo.SegundosRetryAfter = RetryAfter;
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.ServiceUnavailable, "sin catálogos el formulario no ofrece texto libre");
        var lecturasAlBloquear = LecturasDeCatalogos;

        // Durante el bloqueo, los catálogos no salen de Cotifly (429 local)...
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.ServiceUnavailable);
        LecturasDeCatalogos.Should().Be(lecturasAlBloquear, "el cupo es por rol: reintentar dentro del Retry-After solo gastaría cupo");

        // ...pero el polling de la solicitud sí va a Nexo y aplica el costeo.
        var pollingAntes = ConsultasDelPolling(solicitud);
        _nexo.Costear(solicitud, 1_700_000m);
        await _api.ProcesarPollingAsync(folio);
        ConsultasDelPolling(solicitud).Should().BeGreaterThan(pollingAntes, "GET /api/solicitudes/{id} no tiene límite en Nexo");
        (await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT estadoCosteo FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("autocosteo");

        // Se respeta el Retry-After de Nexo (45 s, no los 30 por defecto): a los 44 s sigue bloqueado; pasado, se vuelve a leer.
        _tiempoCupo.Avanzar(TimeSpan.FromSeconds(RetryAfter - 1));
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.ServiceUnavailable);
        LecturasDeCatalogos.Should().Be(lecturasAlBloquear);

        _tiempoCupo.Avanzar(TimeSpan.FromSeconds(2));
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.OK);
        LecturasDeCatalogos.Should().BeGreaterThan(lecturasAlBloquear);
    }

    /// <summary>
    /// Deuda de #122: <c>GET /web/municipios</c> responde 503 si Nexo responde 429, pero los municipios no comparten el cupo de listados
    /// (decisión de HU-023.4): ese 429 no bloquea los catálogos ni lleva <c>Retry-After</c>, y un cupo bloqueado por un catálogo no
    /// deja sin municipios.
    /// </summary>
    [Fact]
    public async Task Un429DeMunicipiosEs503SinRetryAfterYNoBloqueaElCupoDeCatalogos()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        _nexo.LecturasLimitadas = 1;
        _nexo.SegundosRetryAfter = RetryAfter;
        using (var municipios = await _api.EnviarAsync(HttpMethod.Get, "/web/municipios?q=cal", ana.Access))
        {
            await clsEntornoApiWeb.VerificarContratoAsync(municipios, "/web/municipios", HttpMethod.Get, HttpStatusCode.ServiceUnavailable);
            await clsEntornoApiWeb.VerificarErrorNegocioAsync(municipios, "MUNICIPIOS_NO_DISPONIBLE");
            municipios.Headers.RetryAfter.Should().BeNull("el cupo de Nexo no limita los municipios: no hay espera que anunciar");
        }

        var lecturasAntes = LecturasDeCatalogos;
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.OK, "un 429 de municipios no bloquea el cupo de catálogos");
        LecturasDeCatalogos.Should().BeGreaterThan(lecturasAntes);

        // Al revés: con el cupo de catálogos bloqueado, los municipios siguen saliendo a Nexo.
        _nexo.LecturasLimitadas = 1;
        (await CatalogosAsync(ana)).Should().Be(HttpStatusCode.ServiceUnavailable);
        using var conCupoBloqueado = await _api.EnviarAsync(HttpMethod.Get, "/web/municipios?q=yum", ana.Access);
        await clsEntornoApiWeb.VerificarContratoAsync(conCupoBloqueado, "/web/municipios", HttpMethod.Get, HttpStatusCode.OK);
    }

    private async Task<HttpStatusCode> CatalogosAsync(clsSesionPrueba sesion)
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, "/web/catalogos-flete", sesion.Access);
        return respuesta.StatusCode;
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
