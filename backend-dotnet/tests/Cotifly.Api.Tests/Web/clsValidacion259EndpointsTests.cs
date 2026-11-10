using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// #259 (validación adversarial), por HTTP con SQL Server real y el Nexo falso: una ruta que la Mesa rechazó entera no manda sus conexos a la
/// línea de OTRA ruta (una ruta rechazada no se envía, auditoría M2; una con camiones pero ninguno vigente no tiene destino); finalizar
/// repetido no duplica el POST.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsValidacion259EndpointsTests : IDisposable
{
    private const string Cargue = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m);
    private readonly clsApiWebPruebas _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public clsValidacion259EndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }

    /// <summary>
    /// Dos rutas con el Cargue capturado; la Mesa rechaza la línea de la ruta 1, costea la de la ruta 2 y entrega. La cotización de Nexo
    /// tiene UNA sola línea (la de la ruta 2): el Cargue de la ruta 1 no debe viajar a ella (lo cobraría en la ruta 2).
    /// </summary>
    [Fact]
    public async Task UnaRutaRechazadaEnteraNoMandaSusConexosALaLineaDeOtraRuta()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario(rutas: 2));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.RechazarLinea(solicitud, 0, "Sin operación en la ruta");
        _nexo.CostearLinea(solicitud, 1, 1_000_000m);
        _nexo.Entregar(solicitud);

        await _api.ProcesarPollingAsync(folio);
        await _api.ProcesarPollingAsync(folio);

        var rutas = await RutasAsync(sesion, folio);
        rutas[0].GetProperty("estado_costeo").GetString().Should().Be("rechazada");
        var peticiones = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!);
        peticiones.SelectMany(p => p["items"]!.AsArray()).Should().ContainSingle("solo el Cargue de la ruta 2; el de la ruta rechazada no viaja a la línea de otra ruta");
        rutas[1].GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("enviado");
        (await AgendadaAsync(folio)).Should().BeFalse("la ruta rechazada no cuenta para el invariante de agenda");
        await _api.FinalizarAsync(sesion.Access, folio);
    }

    /// <summary>
    /// Finalizar varias veces seguidas sobre una entregada con el conexo pendiente (Nexo de conexos caído): cada clic responde 409 sin POST, la
    /// agenda no se mueve una vez puesta y, al volver Nexo, una sola vuelta del polling hace UN solo POST.
    /// </summary>
    [Fact]
    public async Task FinalizarRepetidoConElConexoPendienteNoDuplicaElPost()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario(rutas: 1));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.Entregar(solicitud);
        _nexo.ConexosFalla = HttpStatusCode.Forbidden;
        await _api.ProcesarPollingAsync(folio);
        _nexo.ConexosFalla = null;
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"DELETE FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'conexos_envio_nexo' AND input LIKE '%{folio}%'; "
            + $"UPDATE dbo_cotifly.CotizacionesSolicitud SET costeoSiguienteConsultaAt = NULL WHERE folio = '{folio}'");
        var posts = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Count;

        DateTimeOffset? agenda = null;
        for (var i = 0; i < 3; i++)
        {
            using var bloqueada = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, null, await EtagAsync(sesion, folio));
            bloqueada.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var actual = await AgendaAsync(folio);
            actual.Should().NotBeNull();
            if (agenda is not null)
            {
                actual.Should().Be(agenda, "con la agenda ya cerca, finalizar no la vuelve a mover");
            }

            agenda = actual;
        }

        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().HaveCount(posts, "finalizar nunca hace el POST");
        await _api.ProcesarPollingEnParaleloAsync(folio, replicas: 3);
        await _api.ProcesarPollingAsync(folio);

        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().HaveCount(posts + 1, "un solo POST aunque el polling corra en paralelo");
        await _api.FinalizarAsync(sesion.Access, folio);
    }

    // ------------------------------------------------------------------ Apoyo

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<List<JsonElement>> RutasAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return [.. json.GetProperty("rutas").EnumerateArray()];
    }

    private Task<bool> AgendadaAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<bool>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT CAST(IIF(costeoSiguienteConsultaAt IS NULL, 0, 1) AS bit) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    private Task<DateTimeOffset?> AgendaAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<DateTimeOffset?>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT costeoSiguienteConsultaAt FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    /// <summary><paramref name="rutas"/> rutas (un camión cada una) con el Cargue capturado en cada ruta (el formulario de #259).</summary>
    private static string Formulario(int rutas) => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        linea_negocio = "Importacion",
        deadline = "2026-10-15T17:00:00-05:00",
        rutas = Enumerable.Range(0, rutas).Select(i => (object)new
        {
            linea_negocio = "Importacion",
            origen_municipio_id = 76109,
            origen = "Buenaventura",
            destino_municipio_id = 11001,
            destino = "Bogotá D.C.",
            tipologia = "Contenedor",
            clase_viaje = "Contenedor",
            clase_contenedor = "CNT40.HC",
            exclusivo = true,
            consolidado = false,
            retorna_contenedor = false,
            levante_contenedor_diferente = false,
            peso_kg = (20 - i) * 1400,
            valor_mercancia = 250000000,
            mercancias = new object[] { new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 20 - i, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
            servicios_conexos = new object[] { new { id_servicio_conexo = Cargue, cantidad = 2 } },
        }).ToArray(),
    });
}
