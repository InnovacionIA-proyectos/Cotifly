using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// #259: un conexo capturado con la ruta ya no queda en <c>estado_nexo = pendiente</c> para siempre (bloqueando finalizar) cuando la cotización
/// se entrega sin UNA línea de Nexo para el primer camión. Reproducido el 2026-11-06: la Mesa dejó la línea de un camión partida en dos
/// líneas de la cotización con el mismo <c>lineaSolicitudId</c>; el costo las suma (hay precio) pero el envío diferido exigía exactamente una,
/// así que nunca se enviaba, la entrega apagaba el polling y finalizar respondía <c>conexos_pendientes_en_nexo</c> para siempre. Por HTTP con
/// SQL Server real y el Nexo falso.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsConexosHuerfanosNexoEndpointsTests : IDisposable
{
    private const string Cargue = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d";
    private const string Almacenamiento = "1b2c3d4e-5f60-4a71-8b92-a3b4c5d6e7f8";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m)
        .ConServicioConexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", 120_000m);
    private readonly clsApiWebPruebas _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public clsConexosHuerfanosNexoEndpointsTests(clsEntornoApiWeb entorno)
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
    /// Camino de la issue (antes la reproducción): con la línea del camión partida en dos, los conexos van a la PRIMERA por id (no a la
    /// primera de la lista) desde el costeo, una sola vez, y la cotización entregada se finaliza.
    /// </summary>
    [Fact]
    public async Task ConLaLineaDelCamionPartidaEnDosLosConexosVanALaPrimeraPorIdYLaEntregadaSeFinaliza()
    {
        var (sesion, folio, solicitud) = await CapturarAsync();
        await _api.ProcesarPollingAsync(folio);
        (await EventosAsync(folio, null)).Should().Be(0, "sin la línea del primer camión, en costeo se espera sin registro");

        _nexo.LineasPartidas.Add(0);
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        await _api.ProcesarPollingAsync(folio);

        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Single()!;
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaPartidaDe(solicitud, 0), "la primera línea del camión por id, un criterio estable");
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue);

        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);

        var ruta = (await RutasAsync(sesion, folio)).Single();
        ruta.GetProperty("estado_costeo").GetString().Should().Be("entregada");
        ruta.GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("enviado");
        (await AgendadaAsync(folio)).Should().BeFalse("entregada y sin nada pendiente: sale de la agenda");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("ya está en Nexo: no se reenvía");
        await _api.FinalizarAsync(sesion.Access, folio);
        (await FinalizadaAsync(folio)).Should().BeTrue();
    }

    /// <summary>
    /// Camino 2 (antes la reproducción del lote #231): en una entregada con la línea partida, el conexo agregado por lote (<c>en_espera</c>)
    /// llega a Nexo en la siguiente vuelta del polling, a la misma línea, y la cotización sale de la agenda sin dejarlo huérfano.
    /// </summary>
    [Fact]
    public async Task UnConexoDelLoteEnUnaEntregadaConLaLineaPartidaLlegaANexoEnLaSiguienteVuelta()
    {
        var (sesion, folio, solicitud) = await CapturarAsync();
        _nexo.LineasPartidas.Add(0);
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);
        (await AgendadaAsync(folio)).Should().BeFalse();

        using (var lote = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos/lineas", sesion.Access,
                   clsApiWebPruebas.Json(new { todas = true, servicio_id = Almacenamiento, cantidad = 1 }), await EtagAsync(sesion, folio)))
        {
            lote.StatusCode.Should().Be(HttpStatusCode.OK, await lote.Content.ReadAsStringAsync(Ct));
        }

        (await AgendadaAsync(folio)).Should().BeTrue("el lote adelanta la agenda para el envío diferido");
        await _api.ProcesarPollingAsync(folio);

        var peticiones = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!);
        peticiones.Should().HaveCount(2, "el Cargue al entregar y el Almacenamiento del lote");
        var item = peticiones[^1]["items"]!.AsArray().Single()!;
        (item["lineaId"]!.GetValue<string>(), item["servicioConexoId"]!.GetValue<string>()).Should().Be((_nexo.LineaPartidaDe(solicitud, 0)!, Almacenamiento));
        (await RutasAsync(sesion, folio)).Single().GetProperty("servicios_conexos").EnumerateArray()
            .Select(c => c.GetProperty("estado_nexo").GetString()).Should().OnlyContain(e => e == "enviado");
        (await AgendadaAsync(folio)).Should().BeFalse("sin nada pendiente sale de la agenda");
        await _api.FinalizarAsync(sesion.Access, folio);
        (await FinalizadaAsync(folio)).Should().BeTrue();
    }

    /// <summary>
    /// Variante 4a (decisión del usuario del 2026-11-06): la Mesa entrega sin la línea del primer camión vigente (camión sin flete, tratado
    /// como rechazado): los conexos van a la línea del primer camión vigente que sí la tiene. Mientras se costeaba, se esperaba.
    /// </summary>
    [Fact]
    public async Task Variante4a_EntregadaSinLaLineaDelPrimerCamionLosConexosVanAlPrimerCamionVigenteConLinea()
    {
        var (_, folio) = await DosCamionesAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 1, 500_000m);
        await _api.ProcesarPollingAsync(folio);
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().BeEmpty("en costeo se espera la línea del primer camión");

        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);

        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Single()!;
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, 1), "el primer camión vigente CON línea");
        (await EventosAsync(folio, "enviado")).Should().Be(1);
        (await AgendadaAsync(folio)).Should().BeFalse();
    }

    /// <summary>
    /// Variante 4a sin destino: la Mesa entrega y ningún camión de la segunda ruta tiene línea en la cotización de Nexo. Sus conexos quedan
    /// <c>fallido_definitivo</c> (visible, con el aviso de revisarlos en Nexo; no bloquea finalizar), los de la primera ruta llegan, y la
    /// cotización sale de la agenda: nunca queda un <c>pendiente</c> sin nadie que lo procese.
    /// </summary>
    [Fact]
    public async Task Variante4a_EntregadaSinNingunCamionConLineaLosConexosDeLaRutaQuedanFallidoDefinitivo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario(rutas: 2));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.Entregar(solicitud);

        await _api.ProcesarPollingAsync(folio);

        var rutas = await RutasAsync(sesion, folio);
        rutas.Select(r => r.GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString())
            .Should().Equal("enviado", "fallido_definitivo");
        rutas[1].GetProperty("aviso_conexos_nexo").GetString().Should().Contain("Revisa en la cotización de Nexo").And.Contain("Cargue en puerto");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("solo la ruta con línea");
        (await AgendadaAsync(folio)).Should().BeFalse("sin nada pendiente: sale de la agenda");
    }

    /// <summary>
    /// Cotizaciones que ya quedaron atascadas antes de #259 (entregada, conexo sin registro que Nexo no tiene, sin agenda): finalizar responde
    /// 409 <c>conexos_pendientes_en_nexo</c> y la vuelve a encolar SIN enviar nada a Nexo; una vuelta del polling envía el conexo y el
    /// siguiente finalizar pasa.
    /// </summary>
    [Fact]
    public async Task FinalizarReencolaUnaEntregadaAtascadaSinEnviarYTrasUnaVueltaDelPollingSeFinaliza()
    {
        var (sesion, folio, solicitud) = await CapturarAsync();
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.Entregar(solicitud);
        _nexo.ConexosFalla = HttpStatusCode.Forbidden;
        await _api.ProcesarPollingAsync(folio);
        _nexo.ConexosFalla = null;

        // El estado que dejaba el código anterior: el conexo sin registro del envío diferido y la cotización fuera de la agenda.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"DELETE FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'conexos_envio_nexo' AND input LIKE '%{folio}%'; "
            + $"UPDATE dbo_cotifly.CotizacionesSolicitud SET costeoSiguienteConsultaAt = NULL WHERE folio = '{folio}'");
        (await RutasAsync(sesion, folio)).Single().GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("pendiente");
        var posts = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Count;

        using (var bloqueada = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, null, await EtagAsync(sesion, folio)))
        {
            var error = (await VerificarContratoAsync(bloqueada, "/web/cotizaciones/{folio}/finalizar", _post, HttpStatusCode.Conflict)).GetProperty("error");
            error.GetProperty("codigo").GetString().Should().Be("COTIZACION_NO_FINALIZABLE");
            error.GetProperty("detalle").GetProperty("motivo").GetString().Should().Be("conexos_pendientes_en_nexo");
        }

        (await AgendadaAsync(folio)).Should().BeTrue("finalizar la vuelve a encolar para el envío diferido");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().HaveCount(posts, "finalizar nunca hace el POST de conexos");

        await _api.ProcesarPollingAsync(folio);

        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().HaveCount(posts + 1);
        (await RutasAsync(sesion, folio)).Single().GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("enviado");
        (await AgendadaAsync(folio)).Should().BeFalse();
        await _api.FinalizarAsync(sesion.Access, folio);
        (await FinalizadaAsync(folio)).Should().BeTrue();
    }

    // ------------------------------------------------------------------ Apoyo

    /// <summary>Una ruta de un camión con el Cargue capturado, enviada a Nexo (<c>pendiente_costeo</c>).</summary>
    private async Task<(clsSesionPrueba Sesion, string Folio, string Solicitud)> CapturarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        return (sesion, folio, _nexo.SolicitudesCreadas[^1]);
    }

    /// <summary>
    /// Una ruta con el Cargue y DOS camiones (el segundo agregado en la base y enviado con el reintento, como en
    /// <c>clsEnvioConexosNexoEndpointsTests.ConElPrimerCamionDescartadoVanAlSiguienteCamionVigente</c>).
    /// </summary>
    private async Task<(clsSesionPrueba Sesion, string Folio)> DosCamionesAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, tipoVehiculo, carroceria, pesoKg, costoLevanteContenedor, "
            + "estadoCosteo, createdAt, updatedAt) "
            + "SELECT NEWID(), l.id, N'2S2', N'FURGON', 6000, ISNULL(l.valorLevanteContenedor, 0), "
            + "(SELECT TOP 1 v.estadoCosteo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id), "
            + "DATEADD(SECOND, 1, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET() "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = 1");
        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);
        return (sesion, folio);
    }

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

    private Task<bool> FinalizadaAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<bool>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT CAST(IIF(finalizadaAt IS NULL, 0, 1) AS bit) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    private Task<int> EventosAsync(string folio, string? estado) => clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{Cotifly.Aplicacion.Costeo.DTOs.clsEnvioConexosNexo.Herramienta}' "
        + $"AND input LIKE '%{folio}%'" + (estado is null ? string.Empty : $" AND outputResumen LIKE '%\"estado\":\"{estado}\"%'"));

    /// <summary>
    /// <paramref name="rutas"/> rutas (Buenaventura → Bogotá, un camión cada una) con el conexo Cargue (2 unidades) capturado con cada ruta
    /// (el formulario de <c>clsEnvioConexosNexoEndpointsTests</c>).
    /// </summary>
    private static string Formulario(int rutas = 1) => clsApiWebPruebas.Json(new
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
