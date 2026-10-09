using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Integración con el cubicador (HU-021.5, #133) por HTTP con SQL Server real, el Nexo falso y el cubicador falso
/// (<see cref="clsCubicadorFalso"/>; el real aún no tiene <c>POST /recomendaciones</c>, C1): varios camiones por ruta que viajan a Nexo
/// uno por línea y se emparejan con su costo (#134), "sin selección" y "condicionada" como resultado (C6), el respaldo ante la caída o
/// el tiempo agotado, la captura que falla sin respaldo, el nombre de la mercancía copiado del catálogo y <c>GET /web/tipos-mercancia</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCubicadorEndpointsTests : IDisposable
{
    private const string TipoCafe = clsCosteoEndpointsTests.TipoMercancia;
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private const string Cargue = "SC-CARGUE-CUB";
    private const string Almacenamiento = "7a2d3b0f-4c8e-4d66-8b1f-2e3a4b5c6d7e";
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, Cargue, "Cargue en puerto", "logistico", "UND", 150_000m)
        .ConServicioConexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", 120_000m);
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsCubicadorEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add((Guid.Parse(TipoCafe), "Café en estibas"));
        _cubicador.TiposMercancia.Add((Guid.NewGuid(), "Bobinas de acero"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cadena => _entorno.BaseDatos.CadenaCotifly;

    [Fact]
    public async Task VariosCamionesPorRutaViajanANexoUnoPorLineaYCadaUnoRecibeSuFlete()
    {
        // Ruta 1 (28000 kg): dos vehículos; ruta 2 (12500 kg): uno.
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? [clsCubicadorFalso.Fila("3S3", "FURGON", 16_000m), clsCubicadorFalso.Fila("2S3", "ESTACAS", 12_000m)]
            : [clsCubicadorFalso.Fila("2", "FURGON", 12_500m)];
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(dosRutas: true));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        // El cubicador recibió cada ruta con su id, el origen COTIFLY y la llave como Bearer.
        var rutas = (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(LOWER(CAST(l.id AS nvarchar(36))), ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))!.Split(',');
        _cubicador.Recomendaciones.Select(r => r["idLineaSolicitud"]!.GetValue<string>()).Should().Equal(rutas);
        _cubicador.Recomendaciones.Should().OnlyContain(r => r["origenSistema"]!.GetValue<string>() == "COTIFLY");
        _cubicador.Peticiones.Should().OnlyContain(p => p.Autorizacion == "Bearer " + clsCubicadorFalso.ApiKey);
        _cubicador.Recomendaciones[0]["mercancias"]!.AsArray().Should().ContainSingle()
            .Which!["idTipoMercancia"]!.GetValue<string>().Should().Be(TipoCafe);

        // Un camión por fila, con el vehículo, la carrocería, el peso (C2) y el id de la recomendación (C3); la mercancía con el nombre del catálogo.
        // En el orden de la recomendación (la fila 1 es el primer camión: recibe los conexos de la ruta, decisión 10).
        (await CamionesAsync(folio)).Should().Equal("1|3S3|FURGON|16000.00|R", "1|2S3|ESTACAS|12000.00|R", "2|2|FURGON|12500.00|R");
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(m.nombreMercancia, ',') FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("Café en estibas,Café en estibas", "el nombre se copia del catálogo del cubicador por id_tipo_mercancia");

        // Envío por camión (#134): una línea por camión con su vehículo y su peso.
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => (l["tipoVehiculo"]!.GetValue<string>(), l["peso"]!.GetValue<decimal>()))
            .Should().Equal(("3S3", 16_000m), ("2S3", 12_000m), ("2", 12_500m));

        // Emparejamiento (#134) con las líneas barajadas: cada camión recibe el flete de su línea.
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.SemillaOrdenLineas = 20261008;
        int Indice(decimal peso) => lineas.FindIndex(l => l["peso"]!.GetValue<decimal>() == peso);
        _nexo.CostearLinea(solicitud, Indice(16_000m), 1_000_000m);
        _nexo.CostearLinea(solicitud, Indice(12_000m), 800_000m);
        _nexo.CostearLinea(solicitud, Indice(12_500m), 500_000m);
        await api.ProcesarPollingAsync(folio);

        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(CAST(v.pesoKg AS nvarchar(20)), '=', CAST(v.costoFlete AS nvarchar(20))), ',') WITHIN GROUP (ORDER BY v.pesoKg) "
            + $"{DeLosCamiones(folio)}"))
            .Should().Be("12000.00=800000.00,12500.00=500000.00,16000.00=1000000.00");
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena, $"SELECT COUNT(DISTINCT v.lineaSolicitudNexoId) {DeLosCamiones(folio)}")).Should().Be(3);
    }

    [Fact]
    public async Task UnaRutaConTresRecomendacionesViajaEnTresLineasConSuLineaDeNegocioYElConexoVaALaFila1()
    {
        // Escenario cruzado #133 sobre #132, #134, #135 y #137: una ruta (Importacion, 28000 kg) con un conexo capturado; el cubicador
        // recomienda tres vehículos.
        _cubicador.RecomendarSiempre(
            clsCubicadorFalso.Fila("3S3", "FURGON", 12_000m),
            clsCubicadorFalso.Fila("2S3", "ESTACAS", 9_000m),
            clsCubicadorFalso.Fila("2", "FURGON", 7_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(conexo: true));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        // Tres camiones en el orden del cubicador.
        (await CamionesAsync(folio)).Should().Equal("1|3S3|FURGON|12000.00|R", "1|2S3|ESTACAS|9000.00|R", "1|2|FURGON|7000.00|R");

        // Tres líneas a Nexo, en ese orden, cada una con la línea de negocio de Nexo de la ruta.
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => (l["tipoVehiculo"]!.GetValue<string>(), l["peso"]!.GetValue<decimal>(), l["lineaNegocio"]!.GetValue<string>()))
            .Should().Equal(("3S3", 12_000m, "PuertoAInterior"), ("2S3", 9_000m, "PuertoAInterior"), ("2", 7_000m, "PuertoAInterior"));

        // Emparejamiento en el polling con las líneas barajadas: cada camión recibe el flete de SU línea.
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.SemillaOrdenLineas = 20261009;
        int Indice(decimal peso) => lineas.FindIndex(l => l["peso"]!.GetValue<decimal>() == peso);
        _nexo.CostearLinea(solicitud, Indice(12_000m), 1_000_000m);
        _nexo.CostearLinea(solicitud, Indice(9_000m), 800_000m);
        _nexo.CostearLinea(solicitud, Indice(7_000m), 500_000m);
        await api.ProcesarPollingAsync(folio);
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(CAST(v.pesoKg AS nvarchar(20)), '=', CAST(v.costoFlete AS nvarchar(20))), ',') WITHIN GROUP (ORDER BY v.pesoKg) "
            + DeLosCamiones(folio)))
            .Should().Be("7000.00=500000.00,9000.00=800000.00,12000.00=1000000.00");
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena, $"SELECT COUNT(DISTINCT v.lineaSolicitudNexoId) {DeLosCamiones(folio)}")).Should().Be(3);

        // El conexo capturado queda en la ruta, y los conexos de la ruta viajan a la línea del primer camión: la fila 1 del cubicador.
        var antes = await RutaAsync(api, ana, folio);
        antes.GetProperty("servicios_conexos").EnumerateArray().Select(c => c.GetProperty("nombre").GetString()).Should().Equal(["Cargue en puerto"]);
        using var agregar = await ConVersionAsync(api, _post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", ana.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 1, servicio_id = Almacenamiento, cantidad = 1 } } }), await EtagAsync(api, ana, folio));
        await VerificarContratoAsync(agregar, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _post, HttpStatusCode.OK);
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Single()!["lineaId"]!
            .GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, Indice(12_000m)), "decisión 3: el primer camión es la fila 1 del cubicador");

        // Métricas por ruta (#137): una cotización, una ruta, costo = 3 fletes + 2 conexos.
        var ruta = await RutaAsync(api, ana, folio);
        ruta.GetProperty("servicios_conexos").EnumerateArray().Select(c => c.GetProperty("nombre").GetString())
            .Should().BeEquivalentTo(["Cargue en puerto", "Almacenamiento"]);
        var precio = ruta.GetProperty("precio_final_linea").GetDecimal();
        (await clsBaseDatosPruebas.EscalarAsync<decimal>(Cadena,
            $"SELECT l.costoFinal FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be(2_570_000m, "1.000.000 + 800.000 + 500.000 de fletes + 150.000 + 120.000 de conexos");
        precio.Should().BeApproximately(2_570_000m / 0.85m, 0.5m, "margen de política de Importacion (0,15)");

        using var respuesta = await api.EnviarAsync(_get, "/web/metricas/comercial?desde=2026-01-01&hasta=2026-12-31", ana.Access);
        var metricas = await VerificarContratoAsync(respuesta, "/web/metricas/comercial", _get, HttpStatusCode.OK);
        var indicadores = metricas.GetProperty("indicadores");
        indicadores.GetProperty("cantidad").GetInt32().Should().Be(1);
        indicadores.GetProperty("costeo_total").GetDecimal().Should().Be(2_570_000m);
        indicadores.GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio);
        var importacion = metricas.GetProperty("por_linea_negocio").EnumerateArray().Should().ContainSingle().Subject;
        importacion.GetProperty("linea_negocio").GetString().Should().Be("Importacion");
        importacion.GetProperty("cotizaciones").GetInt32().Should().Be(1);
        importacion.GetProperty("rutas").GetInt32().Should().Be(1, "tres camiones son una sola ruta");
        importacion.GetProperty("costeo_total").GetDecimal().Should().Be(2_570_000m);
        importacion.GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio);
        importacion.GetProperty("margen_ponderado").GetDecimal().Should().Be(Math.Round((precio - 2_570_000m) / precio, 4, MidpointRounding.ToEven));
    }

    [Fact]
    public async Task SinSeleccionYCondicionadaSonResultadosYLaSolicitudViajaCompleta()
    {
        _cubicador.RecomendarSiempre(
            clsCubicadorFalso.Fila("3S3", "FURGON", 16_000m, "SIN_SELECCION_AUTOMATICA", "carga extradimensionada"),
            clsCubicadorFalso.Fila("3S3", "FURGON", 12_000m, "CONDICIONADA", "pendiente de AdminTarifas"));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        (await CamionesAsync(folio)).Should().Equal("1|||16000.00|R", "1|3S3|FURGON|12000.00|R");
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Should().HaveCount(2, "la solicitud se envía completa a la Mesa de Costeo de Nexo");
        lineas.Select(l => (l["tipoVehiculo"]?.GetValue<string>(), l["peso"]!.GetValue<decimal>()))
            .Should().BeEquivalentTo([((string?)null, 16_000m), ("3S3", 12_000m)], "sin selección viaja sin vehículo; condicionada, como vino");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SiElCubicadorSeCaeOTardaLaRutaQuedaConUnCamionSinVehiculo(bool tarda)
    {
        if (tarda)
        {
            _cubicador.Demora = TimeSpan.FromSeconds(8);
        }
        else
        {
            _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        }

        var api = Api(new Dictionary<string, string?> { ["Cubicador:TiempoEsperaSegundos"] = "1" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        (await CamionesAsync(folio)).Should().Equal("1|||28000.00|-");
        var linea = _nexo.UltimaAlta!["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["tipoVehiculo"].Should().BeNull();
        linea["peso"]!.GetValue<decimal>().Should().Be(28_000m);
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT TOP 1 m.nombreMercancia FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("Estiba de café", "sin catálogo se conserva el nombre que mandó el cliente");
    }

    [Fact]
    public async Task UnCatalogoColgadoNoRetieneLaCapturaYSeConservaElNombreDelCliente()
    {
        _cubicador.DemoraCatalogo = TimeSpan.FromSeconds(60);
        var api = Api(new Dictionary<string, string?> { ["Cubicador:TiempoMaximoCatalogoSegundos"] = "1" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "el catálogo se corta en 1 s y se lee una sola vez por captura");
        (await CamionesAsync(folio)).Should().Equal(["1|3S3|FURGON|28000.00|R"], "las recomendaciones siguen funcionando");
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT TOP 1 m.nombreMercancia FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("Estiba de café", "sin catálogo se conserva el nombre que mandó el cliente");
        _cubicador.Peticiones.Count(p => p.Metodo == HttpMethod.Get).Should().Be(1, "una sola lectura del catálogo por captura");
    }

    [Fact]
    public async Task SinRespaldoUnCubicadorCaidoNoGuardaNiEnviaNada()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        var api = Api(new Dictionary<string, string?> { ["Cubicador:RespaldoSiFalla"] = "false" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "CUBICADOR_NO_DISPONIBLE");
        (await envio.Content.ReadAsStringAsync(Ct)).Should().NotContain("HTTP").And.NotContain("/recomendaciones", "el detalle interno solo va al log");
        _nexo.UltimaAlta.Should().BeNull("sin camiones no se manda nada a Nexo");
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'"))
            .Should().Be(0);
    }

    [Fact]
    public async Task UnTipoDeMercanciaQueNoEstaEnElCatalogoSeRechaza()
    {
        _cubicador.TiposMercancia.RemoveAll(t => t.Id == Guid.Parse(TipoCafe));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        var cuerpo = JsonNode.Parse(await envio.Content.ReadAsStringAsync(Ct))!;
        envio.StatusCode.Should().Be(HttpStatusCode.BadRequest, cuerpo.ToJsonString());
        var error = cuerpo["error"]!["detalle"]!["errores"]!.AsArray().Should().ContainSingle().Subject!;
        error["codigo"]!.GetValue<string>().Should().Be("TIPO_MERCANCIA_INEXISTENTE");
        error["campo"]!.GetValue<string>().Should().Be("mercancias[0].id_tipo_mercancia");
        _cubicador.Recomendaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task ElCatalogoDeTiposDeMercanciaSaleDelCubicadorYSinElResponde503()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var catalogo = await api.EnviarAsync(_get, "/web/tipos-mercancia", ana.Access);
        var tipos = await VerificarContratoAsync(catalogo, "/web/tipos-mercancia", _get, HttpStatusCode.OK);
        using var sinCubicador = await _entorno.Api.EnviarAsync(_get, "/web/tipos-mercancia",
            (await _entorno.Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access);

        tipos.EnumerateArray().Select(t => t.GetProperty("nombre").GetString()).Should().Equal("Bobinas de acero", "Café en estibas");
        tipos.EnumerateArray().Last().GetProperty("id").GetString().Should().Be(TipoCafe);
        await VerificarContratoAsync(sinCubicador, "/web/tipos-mercancia", _get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(sinCubicador, "CUBICADOR_NO_DISPONIBLE");
    }

    public void Dispose()
    {
        foreach (var api in _apis)
        {
            api.Dispose();
        }

        _nexo.Dispose();
        _cubicador.Dispose();
    }

    private clsApiWebPruebas Api(IDictionary<string, string?>? extra = null)
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, extra: extra, cubicador: _cubicador);
        _apis.Add(api);
        return api;
    }

    private static async Task<JsonElement> RutaAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return json.GetProperty("rutas").EnumerateArray().Should().ContainSingle().Subject;
    }

    private static async Task<string?> EtagAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private static async Task<HttpResponseMessage> ConVersionAsync(clsApiWebPruebas api, HttpMethod metodo, string ruta, string token, string cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await api.Cliente.SendAsync(peticion, Ct);
    }

    private static string DeLosCamiones(string folio) =>
        "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'";

    /// <summary>Camiones del folio como <c>orden de la ruta|vehículo|carrocería|peso|R (con recomendación) o -</c>, en orden de envío.</summary>
    private async Task<IReadOnlyList<string>> CamionesAsync(string folio) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(l.orden, '|', v.tipoVehiculo, '|', v.carroceria, '|', CAST(v.pesoKg AS nvarchar(20)), '|', "
            + "IIF(v.idRecomendacionVehiculo IS NULL, '-', 'R')), ',') WITHIN GROUP (ORDER BY l.orden, v.createdAt, v.id) "
            + DeLosCamiones(folio)))!.Split(',');

    /// <summary>Formulario por rutas: la ruta 1 con 20 estibas de 1400 kg (28000 kg) y, con <paramref name="dosRutas"/>, una ruta 2 de 12500 kg.</summary>
    private static string Formulario(bool dosRutas = false, bool conexo = false)
    {
        var ruta1 = new
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
            peso_kg = 28000,
            valor_mercancia = 250000000,
            mercancias = new object[] { new { id_tipo_mercancia = TipoCafe, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
            servicios_conexos = conexo ? new object[] { new { id_servicio_conexo = Cargue, cantidad = 1 } } : [],
        };
        var ruta2 = new
        {
            linea_negocio = "Exportacion",
            origen_municipio_id = 5001,
            origen = "Medellín",
            destino_municipio_id = 13001,
            destino = "Cartagena",
            tipologia = "Contenedor",
            clase_viaje = "Contenedor",
            clase_contenedor = "CNT20.ST",
            exclusivo = true,
            consolidado = false,
            retorna_contenedor = false,
            levante_contenedor_diferente = false,
            peso_kg = 12500,
            valor_mercancia = 90000000,
            mercancias = new object[] { new { id_tipo_mercancia = TipoCafe, nombre_mercancia = "Café verde", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1250 } },
        };
        return clsApiWebPruebas.Json(new
        {
            tipo = "spot",
            nit_cliente = clsCosteoEndpointsTests.Nit,
            cliente_nombre = "Longyin",
            linea_negocio = "Importacion",
            deadline = "2026-10-15T17:00:00-05:00",
            horas_cargue = 4,
            horas_descargue = 6,
            rutas = dosRutas ? new object[] { ruta1, ruta2 } : new object[] { ruta1 },
        });
    }
}
