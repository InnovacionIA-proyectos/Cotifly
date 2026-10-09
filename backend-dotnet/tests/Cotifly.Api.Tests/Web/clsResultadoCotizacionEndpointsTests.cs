using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}' AND motivoRechazo LIKE '%menos%'"))
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
        (await EscalarAsync($"SELECT COALESCE(CAST(ganada AS nvarchar(5)), 'null') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("null");
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
        (await EscalarAsync($"SELECT CONCAT(CAST(ganada AS nvarchar(5)), '|', COALESCE(motivoRechazo, '-')) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be("1|No aplica");
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
        (await EscalarAsync($"SELECT COALESCE(CAST(ganada AS nvarchar(5)), 'null') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("null");
    }

    [Fact]
    public async Task ElSeguimientoInterinoRespondeUsarResultadoNexoParaUnaCotizacionDeNexo()
    {
        var (sesion, folio, _) = await EnviarAsync(entregar: true);

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/seguimiento", sesion.Access, """{"ganada":true}""");

        respuesta.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(respuesta, "USAR_RESULTADO_NEXO");
        (await EscalarAsync($"SELECT COALESCE(CAST(ganada AS nvarchar(5)), 'null') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("null");
    }

    // HU-013.14 (ADR-018 §17): el resultado y el costeo nunca quedan inconsistentes, gane quien gane la carrera con el polling.
    [Fact]
    public async Task EntregadaEnNexoSinLeerYLuegoGanadaQuedaConPrecioPdfYMetrica()
    {
        var (sesion, folio, solicitud) = await EnviarSinPollingAsync();
        _nexo.Costear(solicitud, 2_550_000m);
        _nexo.Entregar(solicitud);
        (await EscalarAsync($"SELECT estadoCosteo FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("pendiente_costeo", "el polling aún no lo vio");

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"ganada"}""");
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("ganada").GetBoolean().Should().BeTrue();
        json.GetProperty("estado_costeo").GetString().Should().Be("entregada");
        json.GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m, "se leyó y aplicó el costeo en la misma operación");
        _nexo.Resultado(solicitud).Should().Be("Ganada");
        (await EscalarAsync(SqlEstado(folio))).Should().Be("entregada|1|3000000|sin-agenda");

        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        pdf.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(pdf, "PDF_YA_ENVIADO"); // regla del PDF (§11) con resultado; ya no COSTEO_NO_LISTO

        using var metricas = await _api.EnviarAsync(HttpMethod.Get, "/web/metricas/comercial", sesion.Access);
        var indicadores = (await VerificarContratoAsync(metricas, "/web/metricas/comercial", HttpMethod.Get, HttpStatusCode.OK)).GetProperty("indicadores");
        indicadores.GetProperty("ganadas").GetInt32().Should().Be(1);
        indicadores.GetProperty("valor_ganado_cop").GetDecimal().Should().Be(3_000_000m, "la ganada cuenta con su precio, no en cero");
    }

    [Fact]
    public async Task SiElPollingLeeLaEntregaPrimeroElResultadoConservaEsePrecioSinReleer()
    {
        var (sesion, folio, solicitud) = await EnviarSinPollingAsync();
        _nexo.Costear(solicitud, 2_550_000m);
        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);
        var consultasAntes = Consultas(solicitud);

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"perdida","motivo":"Precio"}""");

        (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK)).GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m);
        Consultas(solicitud).Should().Be(consultasAntes, "con el precio ya aplicado no se relee el costeo");
        (await EscalarAsync(SqlEstado(folio))).Should().Be("entregada|0|3000000|sin-agenda");
    }

    [Fact]
    public async Task SiLaRelecturaFallaElPrecioLlegaConElPollingDespuesDelResultado()
    {
        var (sesion, folio, solicitud) = await EnviarSinPollingAsync();
        _nexo.Costear(solicitud, 2_550_000m);
        // Fila de antes de la opción A: autocosteo sin precio ni líneas asociadas (el resultado tiene que releer el costeo).
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoCosteo = 'autocosteo' WHERE folio = '{folio}'");
        _nexo.Entregar(solicitud);
        _nexo.FallarConsultaTrasResultado = true;

        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"ganada"}""");
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("ganada").GetBoolean().Should().BeTrue("Nexo ya tiene el resultado");
        json.GetProperty("estado_costeo").GetString().Should().Be("autocosteo", "sin leer el costo no se marca entregada");
        json.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        (await EscalarAsync($"SELECT IIF(costeoSiguienteConsultaAt IS NULL, 'sin-agenda', 'agendada') FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be("agendada", "el polling la completa");

        _nexo.FallarConsultaTrasResultado = false;
        await _api.ProcesarPollingAsync(folio);

        (await EscalarAsync(SqlEstado(folio))).Should().Be("entregada|1|3000000|sin-agenda", "el precio que llega después del resultado se aplica");
    }

    [Fact]
    public async Task LasEntregadasQueQuedaronSinPrecioSeReparanConLaRelectura()
    {
        // Así quedaba una cotización con el defecto (antes de HU-013.14): entregada, ganada, sin precio y sin agenda.
        var (_, folio, solicitud) = await EnviarSinPollingAsync();
        _nexo.Costear(solicitud, 2_550_000m);
        _nexo.Entregar(solicitud);
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoCosteo = 'entregada', ganada = 1, marcadoAt = SYSDATETIMEOFFSET(), costeoSiguienteConsultaAt = NULL, costeoEnviadoAt = DATEADD(HOUR, -100, SYSDATETIMEOFFSET()) WHERE folio = '{folio}'");
        (await EscalarAsync(SqlDiagnosticoEntregadasSinPrecio + $" AND c.folio = '{folio}'")).Should().Be(folio, "la consulta de diagnóstico del DBA la encuentra");

        await using (var ambito = _api.Factory.Services.CreateAsyncScope())
        {
            (await ambito.ServiceProvider.GetRequiredService<Aplicacion.Costeo.Services.IClsPollingCosteoService>().ReprogramarEntregadasSinPrecioAsync(Ct))
                .Should().BeGreaterThanOrEqualTo(1);
        }

        await _api.ProcesarPollingAsync(folio);

        (await EscalarAsync(SqlEstado(folio))).Should().Be("entregada|1|3000000|sin-agenda", "la relectura aplica el precio aunque pasó la edad máxima y ya tiene resultado");
        (await EscalarAsync(SqlDiagnosticoEntregadasSinPrecio + $" AND c.folio = '{folio}'")).Should().BeNull();
    }

    // HU-013.17 (#127): el cliente desiste antes del costeo; la perdida queda solo en Cotifly.
    [Fact]
    public async Task LaPerdidaSinCosteoQuedaSoloEnCotiflyConIfMatchYCuentaEnLasMetricas()
    {
        var (sesion, folio, solicitud) = await EnviarSinPollingAsync();
        var cuerpo = """{"resultado":"perdida","motivo":"El cliente desistió"}""";

        using var sinIfMatch = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, cuerpo);
        sinIfMatch.StatusCode.Should().Be((HttpStatusCode)428);

        using var costeo = await _api.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var etag = costeo.Headers.ETag!.Tag;
        using var respuesta = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, cuerpo, ifMatch: etag);
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("ganada").GetBoolean().Should().BeFalse();
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        json.GetProperty("motivo_estado_costeo").GetString().Should().StartWith("Perdida solo en Cotifly");
        respuesta.Headers.ETag!.Tag.Should().NotBe(etag, "la versión de negocio subió");
        _nexo.Resultado(solicitud).Should().Be("SinDefinir", "no se envía a Nexo");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.EndsWith("/resultado", StringComparison.Ordinal));

        using var repetida = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, cuerpo, ifMatch: etag);
        repetida.StatusCode.Should().Be(HttpStatusCode.Conflict, "con el If-Match viejo es CONFLICTO_CONCURRENCIA");

        _nexo.Costear(solicitud, 2_550_000m);
        (await _api.ProcesarPollingAsync(folio)).Should().Be(0, "el polling quedó detenido");
        (await EscalarAsync(SqlEstado(folio).Replace("CAST(CAST(precioFinal AS bigint) AS nvarchar(30))", "COALESCE(CAST(precioFinal AS nvarchar(30)), 'sin-precio')", StringComparison.Ordinal)))
            .Should().Be("pendiente_costeo|0|sin-precio|sin-agenda", "si Nexo costea después, el resultado no cambia");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'resultado_cotizacion_sin_costeo' AND input LIKE '%{folio}%'"))
            .Should().Be("1");

        using var metricas = await _api.EnviarAsync(HttpMethod.Get, "/web/metricas/comercial", sesion.Access);
        (await VerificarContratoAsync(metricas, "/web/metricas/comercial", HttpMethod.Get, HttpStatusCode.OK))
            .GetProperty("indicadores").GetProperty("perdidas").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SiNexoYaTieneOtroResultadoCotiflyLoTomaYRespondeConflicto()
    {
        var (sesion, folio, solicitud) = await EnviarAsync(entregar: true);
        using (var primera = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"ganada"}"""))
        {
            primera.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Simula que el guardado en Cotifly falló después del 200 de Nexo: Cotifly quedó sin resultado.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET ganada = NULL, marcadoAt = NULL WHERE folio = '{folio}'");

        using var contraria = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access, """{"resultado":"perdida","motivo":"x"}""");

        contraria.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await contraria.Content.ReadAsStringAsync(Ct)).Should().Contain("RESULTADO_YA_DEFINIDO").And.Contain("ganada");
        _nexo.Resultado(solicitud).Should().Be("Ganada");
        (await EscalarAsync($"SELECT CAST(ganada AS nvarchar(5)) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be("1", "Cotifly queda igual que Nexo");
    }

    /// <summary>La consulta de diagnóstico (solo lectura) documentada para el DBA en ADR-018 §17.</summary>
    private const string SqlDiagnosticoEntregadasSinPrecio = """
        SELECT c.folio FROM dbo_cotifly.CotizacionesSolicitud c
        WHERE c.estadoCosteo = 'entregada' AND c.solicitudNexoId IS NOT NULL AND c.precioFinal IS NULL AND c.pdfPath IS NULL
        """;

    private static string SqlEstado(string folio) =>
        "SELECT CONCAT(estadoCosteo, '|', CAST(ganada AS nvarchar(5)), '|', CAST(CAST(precioFinal AS bigint) AS nvarchar(30)), '|', "
        + $"IIF(costeoSiguienteConsultaAt IS NULL, 'sin-agenda', 'agendada')) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'";

    private int Consultas(string solicitud) =>
        _nexo.Peticiones.Count(p => p.Metodo == HttpMethod.Get && p.Url.AbsolutePath.TrimEnd('/') == $"/api/solicitudes/{solicitud}");

    /// <summary>Envía el formulario sin auto-costeo ni polling: queda en <c>pendiente_costeo</c>.</summary>
    private async Task<(clsSesionPrueba Sesion, string Folio, string Solicitud)> EnviarSinPollingAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        return (sesion, json.GetProperty("folio").GetString()!, _nexo.SolicitudesCreadas[^1]);
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
