using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-041.1 (#231, ADR-025): margen y servicios conexos para varias rutas en una operación, por HTTP contra la API real en memoria, SQL
/// Server en Testcontainers y el Nexo falso. Formulario de 5 rutas (un camión cada una). Cada respuesta se valida contra
/// <c>specs/openapi.yaml</c>; la base confirma lo que quedó guardado: todo o nada, una invalidación del PDF y una auditoría por operación.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCosteoLineasEndpointsTests : IDisposable
{
    private const string Cargue = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d";
    private const string RutaMargen = "/web/cotizaciones/{folio}/costeo/margen/lineas";
    private const string RutaConexo = "/web/cotizaciones/{folio}/costeo/servicios-conexos/lineas";
    private const string RutaQuitar = "/web/cotizaciones/{folio}/costeo/servicios-conexos/lineas/quitar";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m);
    private readonly clsApiWebPruebas _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public clsCosteoLineasEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task ElMismoMargenACincoRutasEnUnaLlamadaConUnaAuditoriaYUnaInvalidacionDelPdf()
    {
        var (sesion, folio) = await CosteadaAsync();
        await PdfAnteriorAHu036Async(folio);
        var etag = await EtagAsync(sesion, folio);

        using var respuesta = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
            clsApiWebPruebas.Json(new { lineas = new[] { 1, 2, 3, 4, 5 }, margen = 0.20m }), etag);

        var costeo = await VerificarContratoAsync(respuesta, RutaMargen, _put, HttpStatusCode.OK);
        respuesta.Headers.ETag?.Tag.Should().NotBe(etag);
        var rutas = costeo.GetProperty("rutas").EnumerateArray().ToList();
        rutas.Should().HaveCount(5);
        rutas.Should().OnlyContain(r => r.GetProperty("margen_aplicado").GetDecimal() == 0.20m && r.GetProperty("origen_margen").GetString() == "negociado");
        rutas.Should().OnlyContain(r => Math.Abs(r.GetProperty("precio_final_linea").GetDecimal() - r.GetProperty("costo_final").GetDecimal() / 0.80m) < 1m,
            "costo / (1 − 0,20), a pesos enteros");
        costeo.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(5, "un ajuste por ruta");
        (await EscalarAsync<int>($"SELECT COUNT(DISTINCT lineaSolicitudId) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(5);
        (await HistorialPdfAsync(folio)).Should().Be(1, "una sola invalidación del PDF");
        (await AuditoriasAsync(clsCosteoLineasService.HerramientaMargen, folio)).Should().Be(1, "una sola auditoría");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{clsAjusteMargenCosteoService.HerramientaAuditoria}' AND input LIKE '%{folio}%'"))
            .Should().Be(0, "no pasa por el ajuste de una ruta");
    }

    [Fact]
    public async Task RevertirAlSugeridoEnTodasVuelveCadaRutaASuMargenYOrigen()
    {
        var (sesion, folio) = await CosteadaAsync();
        using (var subir = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
                   clsApiWebPruebas.Json(new { lineas = new[] { 2, 4 }, margen = 0.30m }), await EtagAsync(sesion, folio)))
        {
            subir.StatusCode.Should().Be(HttpStatusCode.OK, await subir.Content.ReadAsStringAsync(Ct));
        }

        using var respuesta = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
            clsApiWebPruebas.Json(new { todas = true, revertir_sugerido = true }), await EtagAsync(sesion, folio));

        var rutas = (await VerificarContratoAsync(respuesta, RutaMargen, _put, HttpStatusCode.OK)).GetProperty("rutas").EnumerateArray().ToList();
        rutas.Should().OnlyContain(r => r.GetProperty("margen_aplicado").GetDecimal() == r.GetProperty("margen_sugerido").GetDecimal()
                                        && r.GetProperty("origen_margen").GetString() != "negociado");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(2, "volver al sugerido no es un ajuste negociado");
        (await AuditoriasAsync(clsCosteoLineasService.HerramientaMargen, folio)).Should().Be(2);
    }

    [Fact]
    public async Task UnaRutaSinPrecioEvitaElCambioEnLasDemasYDiceCual()
    {
        var (sesion, folio) = await EnviadaAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        foreach (var indice in new[] { 0, 1, 2, 4 })
        {
            _nexo.CostearLinea(solicitud, indice, 850_000m);
        }

        await _api.ProcesarPollingAsync(folio);

        using var respuesta = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
            clsApiWebPruebas.Json(new { todas = true, margen = 0.20m }), await EtagAsync(sesion, folio));

        var error = (await VerificarContratoAsync(respuesta, RutaMargen, _put, HttpStatusCode.Conflict)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("COSTEO_NO_LISTO");
        error.GetProperty("detalle").GetProperty("linea_orden").GetInt32().Should().Be(4);
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(0);
        (await EscalarAsync<int>("SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c "
            + $"ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND l.origenMargen = 'negociado'")).Should().Be(0, "ninguna ruta cambió");
        (await AuditoriasAsync(clsCosteoLineasService.HerramientaMargen, folio)).Should().Be(1, "el rechazo también se audita, una vez");
    }

    [Fact]
    public async Task SinIfMatchConUnoViejoOAjenaNoCambiaNada()
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);
        var cuerpo = clsApiWebPruebas.Json(new { todas = true, margen = 0.20m });

        using var sinIfMatch = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access, cuerpo);
        (await VerificarContratoAsync(sinIfMatch, RutaMargen, _put, HttpStatusCode.PreconditionRequired))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("PRECONDICION_REQUERIDA");

        using var viejo = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access, cuerpo, "\"999\"");
        (await VerificarContratoAsync(viejo, RutaMargen, _put, HttpStatusCode.Conflict))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("CONFLICTO_CONCURRENCIA");

        var otro = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var ajena = await _api.EnviarAsync(_post, Url(RutaConexo, folio), otro.Access,
            clsApiWebPruebas.Json(new { todas = true, servicio_id = Cargue, cantidad = 1 }), etag);
        await VerificarContratoAsync(ajena, RutaConexo, _post, HttpStatusCode.NotFound);

        (await EtagAsync(sesion, folio)).Should().Be(etag, "nada cambió");
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(0);
    }

    [Fact]
    public async Task FinalizadaPideLaVersionNuevaEnLasTresOperaciones()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarAsync(sesion.Access, folio);
        var etag = await EtagAsync(sesion, folio);

        using var margen = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access, clsApiWebPruebas.Json(new { todas = true, margen = 0.2m }), etag);
        using var agregar = await _api.EnviarAsync(_post, Url(RutaConexo, folio), sesion.Access,
            clsApiWebPruebas.Json(new { todas = true, servicio_id = Cargue, cantidad = 1 }), etag);
        using var quitar = await _api.EnviarAsync(_post, Url(RutaQuitar, folio), sesion.Access, clsApiWebPruebas.Json(new { todas = true, servicio_id = Cargue }), etag);

        foreach (var (respuesta, ruta, metodo) in new[] { (margen, RutaMargen, _put), (agregar, RutaConexo, _post), (quitar, RutaQuitar, _post) })
        {
            var error = (await VerificarContratoAsync(respuesta, ruta, metodo, HttpStatusCode.Conflict)).GetProperty("error");
            error.GetProperty("codigo").GetString().Should().Be("PDF_YA_ENVIADO");
            error.GetProperty("detalle").GetProperty("accion").GetString().Should().Be("nueva_version");
        }
    }

    [Fact]
    public async Task UnConexoAgregadoATodasSeIdentificaPorServicioIdYElPollingLoLlevaANexoUnaVezPorRuta()
    {
        var (sesion, folio) = await CosteadaAsync(entregar: true);
        (await AgendadaAsync(folio)).Should().BeFalse("entregada: el polling se detuvo");
        var etag = await EtagAsync(sesion, folio);
        var cuerpo = clsApiWebPruebas.Json(new { todas = true, servicio_id = Cargue, cantidad = 2 });

        using var respuesta = await ConClaveAsync(_post, Url(RutaConexo, folio), sesion.Access, cuerpo, etag, "clave-conexos-0001");

        var costeo = await VerificarContratoAsync(respuesta, RutaConexo, _post, HttpStatusCode.OK);
        var conexos = costeo.GetProperty("rutas").EnumerateArray().Select(r => r.GetProperty("servicios_conexos").EnumerateArray().Single()).ToList();
        conexos.Should().HaveCount(5);
        conexos.Should().OnlyContain(c => c.GetProperty("servicio_id").GetString() == Cargue && c.GetProperty("estado_nexo").GetString() == "pendiente");
        (await AuditoriasAsync(clsCosteoLineasService.HerramientaAgregarConexo, folio)).Should().Be(1);
        (await AgendadaAsync(folio)).Should().BeTrue("vuelve a la agenda solo para el envío diferido");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(_nexo.SolicitudesCreadas[^1])!).Should().BeEmpty("la petición no llama al POST de Nexo");

        // Repetir con la misma clave devuelve la misma respuesta, sin ejecutar (ni auditar) otra vez.
        using var repetida = await ConClaveAsync(_post, Url(RutaConexo, folio), sesion.Access, cuerpo, etag, "clave-conexos-0001");
        (await VerificarContratoAsync(repetida, RutaConexo, _post, HttpStatusCode.OK)).GetProperty("row_version").GetString()
            .Should().Be(costeo.GetProperty("row_version").GetString());
        (await AuditoriasAsync(clsCosteoLineasService.HerramientaAgregarConexo, folio)).Should().Be(1);

        await _api.ProcesarPollingAsync(folio);

        var items = _nexo.PeticionesConexos(_nexo.CotizacionDe(_nexo.SolicitudesCreadas[^1])!).SelectMany(p => p["items"]!.AsArray()).ToList();
        items.Should().HaveCount(5, "uno por ruta, pegado a la línea de su camión");
        items.Should().OnlyContain(i => i!["servicioConexoId"]!.GetValue<string>() == Cargue);
        items.Select(i => i!["lineaId"]!.GetValue<string>()).Distinct().Should().HaveCount(5);
        (await RutasAsync(sesion, folio)).SelectMany(r => r.GetProperty("servicios_conexos").EnumerateArray())
            .Should().OnlyContain(c => c.GetProperty("estado_nexo").GetString() == "enviado");

        await _api.ProcesarPollingAsync(folio);
        _nexo.PeticionesConexos(_nexo.CotizacionDe(_nexo.SolicitudesCreadas[^1])!).SelectMany(p => p["items"]!.AsArray()).Should().HaveCount(5, "una sola vez");
        await _api.FinalizarAsync(sesion.Access, folio);
    }

    [Fact]
    public async Task QuitarDeVariasRutasPorServicioIdUnaSolaAuditoria()
    {
        var (sesion, folio) = await CosteadaAsync();
        using (var agregar = await _api.EnviarAsync(_post, Url(RutaConexo, folio), sesion.Access,
                   clsApiWebPruebas.Json(new { lineas = new[] { 1, 2, 3 }, servicio_id = Cargue, cantidad = 1 }), await EtagAsync(sesion, folio)))
        {
            agregar.StatusCode.Should().Be(HttpStatusCode.OK, await agregar.Content.ReadAsStringAsync(Ct));
        }

        using var respuesta = await _api.EnviarAsync(_post, Url(RutaQuitar, folio), sesion.Access,
            clsApiWebPruebas.Json(new { lineas = new[] { 1, 3 }, servicio_id = Cargue }), await EtagAsync(sesion, folio));

        var rutas = (await VerificarContratoAsync(respuesta, RutaQuitar, _post, HttpStatusCode.OK)).GetProperty("rutas").EnumerateArray().ToList();
        rutas.Select(r => r.GetProperty("servicios_conexos").GetArrayLength()).Should().Equal(0, 1, 0, 0, 0);
        (await AuditoriasAsync(clsCosteoLineasService.HerramientaQuitarConexo, folio)).Should().Be(1);
    }

    [Fact]
    public async Task UnaRutaRechazadaPedidaBloqueaYConTodasSeOmite()
    {
        var (sesion, folio) = await EnviadaAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        foreach (var indice in new[] { 0, 1, 2, 3 })
        {
            _nexo.CostearLinea(solicitud, indice, 850_000m);
        }

        _nexo.RechazarLinea(solicitud, 4, "Sin operación en la ruta");
        await _api.ProcesarPollingAsync(folio);

        using var pedida = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
            clsApiWebPruebas.Json(new { lineas = new[] { 1, 5 }, margen = 0.2m }), await EtagAsync(sesion, folio));
        var error = (await VerificarContratoAsync(pedida, RutaMargen, _put, HttpStatusCode.Conflict)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("RUTA_RECHAZADA");
        error.GetProperty("detalle").GetProperty("linea_orden").GetInt32().Should().Be(5);

        using var todas = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
            clsApiWebPruebas.Json(new { todas = true, margen = 0.2m }), await EtagAsync(sesion, folio));
        await VerificarContratoAsync(todas, RutaMargen, _put, HttpStatusCode.OK);
        (await EscalarAsync<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(4, "la rechazada no cuenta");
    }

    [Fact]
    public async Task UnaFormaInvalidaEsErrorWeb()
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        using var ambas = await _api.EnviarAsync(_put, Url(RutaMargen, folio), sesion.Access,
            clsApiWebPruebas.Json(new { todas = true, lineas = new[] { 1 }, margen = 0.2m }), etag);
        using var repetida = await _api.EnviarAsync(_post, Url(RutaConexo, folio), sesion.Access,
            clsApiWebPruebas.Json(new { lineas = new[] { 1, 1 }, servicio_id = Cargue, cantidad = 1 }), etag);

        (await VerificarContratoAsync(ambas, RutaMargen, _put, HttpStatusCode.BadRequest)).GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
        (await VerificarContratoAsync(repetida, RutaConexo, _post, HttpStatusCode.BadRequest)).GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviadaAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        _nexo.LineasDe(_nexo.SolicitudesCreadas[^1]).Should().HaveCount(5, "un camión por ruta");
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    /// <summary>Las 5 rutas costeadas por Nexo (flete 850.000 cada una) y leídas por el polling.</summary>
    private async Task<(clsSesionPrueba Sesion, string Folio)> CosteadaAsync(bool entregar = false)
    {
        var (sesion, folio) = await EnviadaAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        for (var i = 0; i < 5; i++)
        {
            _nexo.CostearLinea(solicitud, i, 850_000m);
        }

        if (entregar)
        {
            _nexo.Entregar(solicitud);
        }

        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    /// <summary>Un PDF generado antes de HU-036 (sin finalizar): el margen lo invalida.</summary>
    private Task PdfAnteriorAHu036Async(string folio) => clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
        $"UPDATE dbo_cotifly.CotizacionesSolicitud SET pdfPath = '{folio}.pdf', pdfDesactualizado = 0 WHERE folio = '{folio}'");

    private Task<int> HistorialPdfAsync(string folio) => EscalarAsync<int>(
        "SELECT COUNT(*) FROM dbo_cotifly.CotizacionPdfsHistorial h JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = h.cotizacionId "
        + $"WHERE c.folio = '{folio}'");

    private Task<int> AuditoriasAsync(string herramienta, string folio) => EscalarAsync<int>(
        $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{herramienta}' AND input LIKE '%{folio}%'");

    private Task<bool> AgendadaAsync(string folio) => EscalarAsync<bool>(
        $"SELECT CAST(IIF(costeoSiguienteConsultaAt IS NULL, 0, 1) AS bit) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_entorno.BaseDatos.CadenaCotifly, sql);

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

    private async Task<HttpResponseMessage> ConClaveAsync(HttpMethod metodo, string ruta, string token, string cuerpo, string? ifMatch, string clave)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    private static string Url(string plantilla, string folio) => plantilla.Replace("{folio}", folio, StringComparison.Ordinal);

    /// <summary>Cinco rutas de la misma línea de negocio (Importacion), un camión cada una, sin conexos.</summary>
    private static string Formulario() => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        linea_negocio = "Importacion",
        deadline = "2026-10-15T17:00:00-05:00",
        horas_cargue = 4,
        horas_descargue = 6,
        rutas = new object[]
        {
            Ruta(76109, "Buenaventura", 11001, "Bogotá D.C."), Ruta(5001, "Medellín", 13001, "Cartagena"), Ruta(76001, "Cali", 66001, "Pereira"),
            Ruta(8001, "Barranquilla", 68001, "Bucaramanga"), Ruta(73001, "Ibagué", 41001, "Neiva"),
        },
    });

    private static object Ruta(int origenId, string origen, int destinoId, string destino) => new
    {
        origen_municipio_id = origenId,
        origen,
        destino_municipio_id = destinoId,
        destino,
        tipologia = "Contenedor",
        clase_viaje = "Contenedor",
        clase_contenedor = "CNT40.HC",
        exclusivo = true,
        consolidado = false,
        retorna_contenedor = false,
        levante_contenedor_diferente = false,
        peso_kg = 12500,
        valor_mercancia = 90000000,
        mercancias = new object[]
        {
            new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1250 },
        },
    };

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
