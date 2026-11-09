using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-046.3 (#269): la política de seguros por HTTP con SQL Server real (script 21 con la carga aprobada) y el Nexo falso. El bloque
/// <c>politica_seguros</c> de <c>GET .../costeo</c>, el <c>codigoServicio</c> guardado al agregar un conexo (y completado si falta) y el
/// bloqueo de <c>POST .../finalizar</c> con <c>medidas_obligatorias_faltantes</c> y la justificación que lo salta (auditada).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPoliticaSegurosEndpointsTests : IDisposable
{
    private const string RutaCosteo = "/web/cotizaciones/{folio}/costeo";
    private const string RutaFinalizar = "/web/cotizaciones/{folio}/finalizar";
    private const string AcompananteTotal = "8a0f7a52-46e1-4c1e-9a0b-0e1f2a3b4c01";
    private const string AcompananteParcial = "8a0f7a52-46e1-4c1e-9a0b-0e1f2a3b4c02";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(AcompananteTotal, "AVT_AUTO", "Acompañante vehicular total en auto", "adicional", "servicio", 300_000m)
        .ConServicioConexo(AcompananteParcial, "AVP_MOTO", "Acompañante vehicular parcial en moto", "adicional", "servicio", 120_000m);
    private readonly clsApiWebPruebas _api;
    private readonly clsEntornoApiWeb _entorno;

    public clsPoliticaSegurosEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElCosteoTraeElTramoLasRecomendacionesYLaObligacionPendienteConSusConexos()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);

        var ruta = (await CosteoAsync(sesion, folio)).GetProperty("rutas")[0];

        var politica = ruta.GetProperty("politica_seguros");
        politica.GetProperty("tramo").GetProperty("nombre").GetString().Should().Be("> 350 – 500 M");
        politica.GetProperty("tramo").GetProperty("valor_desde").GetDecimal().Should().Be(350_000_000m);
        politica.GetProperty("tramo").GetProperty("valor_hasta").GetDecimal().Should().Be(500_000_000m);
        politica.GetProperty("medidas_recomendadas").EnumerateArray().Select(m => m.GetProperty("codigo").GetString())
            .Should().BeEquivalentTo(["esquema_basico", "gps", "candado_satelital", "restriccion_nocturna", "incluir_seguro"]);
        var obligatoria = politica.GetProperty("medidas_obligatorias").EnumerateArray().Should().ContainSingle().Subject;
        obligatoria.GetProperty("codigo").GetString().Should().Be("acompanante_vehicular");
        obligatoria.GetProperty("nombre").GetString().Should().Be("Acompañante vehicular (parcial o total)");
        obligatoria.GetProperty("estado").GetString().Should().Be("pendiente");
        obligatoria.GetProperty("conexos").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("AVP_MOTO", "AVP_MOTO_ARMADA", "AVT_AUTO", "AVT_MOTO", "AVT_MOTO_ARMADA");
        politica.GetProperty("aviso").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task FinalizarSinElAcompananteRespondeMedidasObligatoriasFaltantesYConJustificacionFinalizaYLaAudita()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);

        using var sinJustificacion = await _api.EnviarAsync(_post, Url(RutaFinalizar, folio), sesion.Access, null, await EtagAsync(sesion, folio));

        var error = (await VerificarContratoAsync(sinJustificacion, RutaFinalizar, _post, HttpStatusCode.Conflict)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("COTIZACION_NO_FINALIZABLE");
        var detalle = error.GetProperty("detalle");
        detalle.GetProperty("motivo").GetString().Should().Be("medidas_obligatorias_faltantes");
        var faltante = detalle.GetProperty("rutas")[0];
        faltante.GetProperty("linea_orden").GetInt32().Should().Be(1);
        faltante.GetProperty("medidas")[0].GetProperty("codigo").GetString().Should().Be("acompanante_vehicular");
        faltante.GetProperty("medidas")[0].GetProperty("conexos").GetArrayLength().Should().Be(5);

        using var vacia = await _api.EnviarAsync(_post, Url(RutaFinalizar, folio), sesion.Access,
            """{"justificacion":"   "}""", await EtagAsync(sesion, folio));
        vacia.StatusCode.Should().Be(HttpStatusCode.Conflict, "una justificación vacía no salta el bloqueo");

        using var larga = await _api.EnviarAsync(_post, Url(RutaFinalizar, folio), sesion.Access,
            clsApiWebPruebas.Json(new { justificacion = new string('x', 1001) }), await EtagAsync(sesion, folio));
        await VerificarContratoAsync(larga, RutaFinalizar, _post, HttpStatusCode.BadRequest);

        using var justificada = await _api.EnviarAsync(_post, Url(RutaFinalizar, folio), sesion.Access,
            """{"justificacion":"Riesgo aprobó el viaje sin acompañante (prueba)."}""", await EtagAsync(sesion, folio));

        var resumen = await VerificarContratoAsync(justificada, RutaFinalizar, _post, HttpStatusCode.OK);
        resumen.GetProperty("finalizada_at").ValueKind.Should().Be(JsonValueKind.String);
        (await EscalarAsync<int>("SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'finalizar_cotizacion' AND exito = 1 "
            + $"AND input LIKE '%{folio}%' AND input LIKE '%Riesgo aprobó el viaje sin acompañante%' "
            + "AND outputResumen LIKE '%acompanante_vehicular%' AND outputResumen NOT LIKE '%400000000%'"))
            .Should().Be(1, "la justificación y la medida omitida quedan auditadas, sin montos");
        (await CosteoAsync(sesion, folio)).GetProperty("rutas")[0].GetProperty("politica_seguros").ValueKind
            .Should().Be(JsonValueKind.Null, "una finalizada no se recalcula (decisión 7)");
    }

    [Fact]
    public async Task AgregarElAcompananteGuardaSuCodigoCumpleLaObligacionYFinalizaSinJustificacion()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);

        using var agregar = await _api.EnviarAsync(_post, Url("/web/cotizaciones/{folio}/costeo/servicios-conexos", folio), sesion.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 1, servicio_id = AcompananteTotal, cantidad = 1 } } }),
            await EtagAsync(sesion, folio));

        await VerificarContratoAsync(agregar, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _post, HttpStatusCode.OK);
        // La fila importada de Nexo no trae el código: sale del índice del catálogo (calentado en segundo plano, sin la caché de costos).
        await _api.Factory.Services.GetRequiredService<clsAlmacenCategoriasConexos>().UltimaLectura.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var costeo = await CosteoAsync(sesion, folio);
        costeo.GetProperty("rutas")[0].GetProperty("politica_seguros").GetProperty("medidas_obligatorias")[0]
            .GetProperty("estado").GetString().Should().Be("cumplida");
        (await CodigosAsync(folio)).Should().Be("AVT_AUTO", "la lectura del costeo guarda el código del catálogo");

        await _api.FinalizarAsync(sesion.Access, folio);
    }

    [Fact]
    public async Task AgregarPorLineasGuardaElCodigoYUnAcompananteParcialCumpleDe350A500()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);

        using var agregar = await _api.EnviarAsync(_post, Url("/web/cotizaciones/{folio}/costeo/servicios-conexos/lineas", folio), sesion.Access,
            clsApiWebPruebas.Json(new { lineas = new[] { 1 }, servicio_id = AcompananteParcial, cantidad = 1 }), await EtagAsync(sesion, folio));

        var costeo = await VerificarContratoAsync(agregar, "/web/cotizaciones/{folio}/costeo/servicios-conexos/lineas", _post, HttpStatusCode.OK);
        costeo.GetProperty("rutas")[0].GetProperty("politica_seguros").GetProperty("medidas_obligatorias")[0]
            .GetProperty("estado").GetString().Should().Be("cumplida");
        (await CodigosAsync(folio)).Should().Be("AVP_MOTO");
    }

    [Fact]
    public async Task UnConexoSinCodigoSeCompletaConElCatalogoAlLeerElCosteo()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);
        using (var agregar = await _api.EnviarAsync(_post, Url("/web/cotizaciones/{folio}/costeo/servicios-conexos/lineas", folio), sesion.Access,
                   clsApiWebPruebas.Json(new { lineas = new[] { 1 }, servicio_id = AcompananteTotal, cantidad = 1 }), await EtagAsync(sesion, folio)))
        {
            agregar.StatusCode.Should().Be(HttpStatusCode.OK, await agregar.Content.ReadAsStringAsync(Ct));
        }

        // Una fila anterior al script 21: sin código, el acompañante no cumple hasta que se lee el catálogo.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE s SET codigoServicio = NULL FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l "
            + $"ON l.id = s.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");
        var etagAntes = await EtagAsync(sesion, folio);
        var versionAntes = await VersionNegocioAsync(folio);
        await _api.Factory.Services.GetRequiredService<clsAlmacenCategoriasConexos>().UltimaLectura.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using var respuesta = await _api.EnviarAsync(_get, Url(RutaCosteo, folio), sesion.Access);
        var costeo = await VerificarContratoAsync(respuesta, RutaCosteo, _get, HttpStatusCode.OK);

        costeo.GetProperty("rutas")[0].GetProperty("politica_seguros").GetProperty("medidas_obligatorias")[0]
            .GetProperty("estado").GetString().Should().Be("cumplida");
        (await CodigosAsync(folio)).Should().Be("AVT_AUTO", "la lectura del catálogo completa la fila en NULL");
        // B2 de la auditoría: el GET que escribe el código no cambia el ETag ni la versión de negocio.
        respuesta.Headers.ETag?.Tag.Should().Be(etagAntes);
        (await VersionNegocioAsync(folio)).Should().Be(versionAntes);
        (await EtagAsync(sesion, folio)).Should().Be(etagAntes);
    }

    [Fact]
    public async Task EnUnaFinalizadaElGetCompletaElCodigoSinCambiarElEtagYSinBloqueDePolitica()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);
        using (var agregar = await _api.EnviarAsync(_post, Url("/web/cotizaciones/{folio}/costeo/servicios-conexos", folio), sesion.Access,
                   clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 1, servicio_id = AcompananteTotal, cantidad = 1 } } }),
                   await EtagAsync(sesion, folio)))
        {
            agregar.StatusCode.Should().Be(HttpStatusCode.OK, await agregar.Content.ReadAsStringAsync(Ct));
        }

        using (var finalizar = await _api.EnviarAsync(_post, Url(RutaFinalizar, folio), sesion.Access,
                   """{"justificacion":"Prueba de la finalizada."}""", await EtagAsync(sesion, folio)))
        {
            finalizar.StatusCode.Should().Be(HttpStatusCode.OK, await finalizar.Content.ReadAsStringAsync(Ct));
        }

        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE s SET codigoServicio = NULL FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l "
            + $"ON l.id = s.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");
        var etagAntes = await EtagAsync(sesion, folio);
        await _api.Factory.Services.GetRequiredService<clsAlmacenCategoriasConexos>().UltimaLectura.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var costeo = await CosteoAsync(sesion, folio);

        costeo.GetProperty("rutas")[0].GetProperty("politica_seguros").ValueKind.Should().Be(JsonValueKind.Null, "una finalizada no se recalcula");
        (await CodigosAsync(folio)).Should().Be("AVT_AUTO", "el dato del catálogo se completa también en una finalizada");
        (await EtagAsync(sesion, folio)).Should().Be(etagAntes);
    }

    [Fact]
    public async Task FinalizarConUnCuerpoMalformadoEs400SinCambios()
    {
        var (sesion, folio) = await CosteadaAsync(400_000_000);

        using var respuesta = await _api.EnviarAsync(_post, Url(RutaFinalizar, folio), sesion.Access, """{"justificacion": 12""",
            await EtagAsync(sesion, folio));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await EscalarAsync<string>($"SELECT CONVERT(nvarchar(40), finalizadaAt) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task UnaRutaSinValorDeMercanciaOConCeroTraeElAvisoYNoBloqueaFinalizar(int? valor)
    {
        var (sesion, folio) = await CosteadaAsync(valor);

        var politica = (await CosteoAsync(sesion, folio)).GetProperty("rutas")[0].GetProperty("politica_seguros");

        politica.GetProperty("tramo").ValueKind.Should().Be(JsonValueKind.Null);
        politica.GetProperty("aviso").GetString().Should().Contain("no tiene valor de la mercancía");
        politica.GetProperty("medidas_obligatorias").GetArrayLength().Should().Be(0);
        await _api.FinalizarAsync(sesion.Access, folio);
    }

    [Fact]
    public async Task EnElLimiteDe250MLaRutaCaeEnElTramoDeAbajoYSoloRecomienda()
    {
        var (sesion, folio) = await CosteadaAsync(250_000_000);

        var politica = (await CosteoAsync(sesion, folio)).GetProperty("rutas")[0].GetProperty("politica_seguros");

        politica.GetProperty("tramo").GetProperty("nombre").GetString().Should().Be("> 150 – 250 M");
        politica.GetProperty("medidas_obligatorias").GetArrayLength().Should().Be(0);
        await _api.FinalizarAsync(sesion.Access, folio);
    }

    private static string Url(string plantilla, string folio) => plantilla.Replace("{folio}", folio, StringComparison.Ordinal);

    /// <summary><c>codigoServicio</c> de los conexos del folio, separados por coma.</summary>
    private Task<string?> CodigosAsync(string folio) => EscalarAsync<string>(
        "SELECT STRING_AGG(ISNULL(s.codigoServicio, 'NULL'), ',') FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l "
        + $"ON l.id = s.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

    private Task<int> VersionNegocioAsync(string folio) =>
        EscalarAsync<int>($"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_entorno.BaseDatos.CadenaCotifly, sql);

    private async Task<(clsSesionPrueba Sesion, string Folio)> CosteadaAsync(int? valorMercancia)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = clsCosteoEndpointsTests.Formulario().Replace("\"valor_mercancia\":250000000",
            $"\"valor_mercancia\":{(valorMercancia is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}", StringComparison.Ordinal);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, formulario);
        var folio = (await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    private async Task<JsonElement> CosteoAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, Url(RutaCosteo, folio), sesion.Access);
        return await VerificarContratoAsync(respuesta, RutaCosteo, _get, HttpStatusCode.OK);
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, Url(RutaCosteo, folio), sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
