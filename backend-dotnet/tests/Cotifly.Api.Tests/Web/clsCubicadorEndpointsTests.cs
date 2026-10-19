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

    /// <summary>Código que el cubicador falso le da a "Café en estibas".</summary>
    private const string CodigoCafe = "CAFE_EN_ESTIBAS";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
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
    public async Task ConLosCodigosDelCubicadorApagadosLasLineasVanSinVehiculoYCadaCamionRecibeSuFlete() =>
        await EnviarYLeerConBanderasAsync(enviarCodigos: false, leerConCodigos: false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SiLaBanderaDeCodigosCambiaEntreElEnvioYElPollingSeEmparejaConLoQueViajo(bool enviarCodigos, bool leerConCodigos) =>
        await EnviarYLeerConBanderasAsync(enviarCodigos, leerConCodigos);

    /// <summary>
    /// Validación de #159: envía con <c>Nexo:EnviarCodigosCubicador</c> = <paramref name="enviarCodigos"/> y hace el polling con OTRA API
    /// (otro despliegue) con la bandera en <paramref name="leerConCodigos"/>. El emparejamiento usa lo que viajó (registro del envío).
    /// </summary>
    private async Task EnviarYLeerConBanderasAsync(bool enviarCodigos, bool leerConCodigos)
    {
        // Contrato de Nexo #297 (por defecto): la línea del camión del cubicador va sin tipoVehiculo ni carroceria (la resuelve la Mesa);
        // Cotifly guarda los códigos y el polling empareja sin compararlos, aunque la Mesa le ponga otro vehículo.
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 12_000m))
            : clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("2", "FURGON", 12_500m));
        var api = Api(new Dictionary<string, string?> { ["Nexo:EnviarCodigosCubicador"] = enviarCodigos ? "true" : "false" });
        var lectura = Api(new Dictionary<string, string?> { ["Nexo:EnviarCodigosCubicador"] = leerConCodigos ? "true" : "false" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(dosRutas: true));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        (await CamionesAsync(folio)).Should().Equal("1|3S3|FURGON|16000.00|R", "1|2S3|ESTACAS|12000.00|R", "2|2|FURGON|12500.00|R");
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        (string?, string?, decimal)[] esperadas = enviarCodigos
            ? [("3S3", "FURGON", 16_000m), ("2S3", "ESTACAS", 12_000m), ("2", "FURGON", 12_500m)]
            : [(null, null, 16_000m), (null, null, 12_000m), (null, null, 12_500m)];
        lineas.Select(l => (l["tipoVehiculo"]?.GetValue<string>(), l["carroceria"]?.GetValue<string>(), l["peso"]!.GetValue<decimal>()))
            .Should().Equal(esperadas);
        lineas.Should().OnlyContain(l => l["posiciones"] == null || l["posiciones"]!.AsArray().All(p => p!["codigoTipoMercancia"] == null));

        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.SemillaOrdenLineas = 20261008;
        if (!enviarCodigos)
        {
            _nexo.AsignarVehiculo(solicitud, 1, "2S2", "ESTACAS"); // la Mesa le pone vehículo a una línea que iba vacía
        }

        _nexo.CostearLinea(solicitud, 0, 1_600_000m);
        _nexo.CostearLinea(solicitud, 1, 1_200_000m);
        _nexo.CostearLinea(solicitud, 2, 1_250_000m);
        await lectura.ProcesarPollingAsync(folio);

        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(CAST(v.pesoKg AS nvarchar(20)), '=', CAST(v.costoFlete AS nvarchar(30))), ',') WITHIN GROUP (ORDER BY v.pesoKg DESC) "
            + "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("16000.00=1600000.00,12500.00=1250000.00,12000.00=1200000.00", "cada camión recibe el flete de su línea");
    }

    [Fact]
    public async Task VariosCamionesPorRutaViajanANexoUnoPorLineaYCadaUnoRecibeSuFlete()
    {
        // Ruta 1 (28000 kg): dos vehículos; ruta 2 (12500 kg): uno.
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 12_000m))
            : clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("2", "FURGON", 12_500m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(dosRutas: true));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        // El cubicador recibió cada ruta con su id, el origen COTIFLY, la clase de viaje como preferencia, el comercial y la llave como Bearer.
        var rutas = (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(LOWER(CAST(l.id AS nvarchar(36))), ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))!.Split(',');
        _cubicador.Recomendaciones.Select(r => r["idLineaSolicitud"]!.GetValue<string>()).Should().Equal(rutas);
        _cubicador.Recomendaciones.Should().OnlyContain(r => r["origenSistema"]!.GetValue<string>() == "COTIFLY");
        _cubicador.Peticiones.Should().OnlyContain(p => p.Autorizacion == "Bearer " + clsCubicadorFalso.ApiKey);
        _cubicador.Recomendaciones.Should().OnlyContain(r => r["claseViajeSeleccionada"]!.GetValue<string>() == "Contenedor");
        _cubicador.Recomendaciones.Should().OnlyContain(r => !string.IsNullOrEmpty(r["usuarioId"]!.GetValue<string>()));
        _cubicador.Recomendaciones[0]["mercancias"]!.AsArray().Should().ContainSingle()
            .Which!["codigoMercancia"]!.GetValue<string>().Should().Be(CodigoCafe, "HU-023: el id_tipo_mercancia se traduce al código del catálogo");

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
            clsCubicadorFalso.Vehiculo("3S3", "FURGON", 12_000m),
            clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 9_000m),
            clsCubicadorFalso.Vehiculo("2", "FURGON", 7_000m));
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
        // El capturado llegó con el envío diferido del polling (decisión 10, #136) y el agregado con el POST: los dos a la fila 1.
        var items = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).SelectMany(p => p["items"]!.AsArray()).Select(i => i!).ToList();
        items.Select(i => i["servicioConexoId"]!.GetValue<string>()).Should().BeEquivalentTo([Cargue, Almacenamiento]);
        var fila1 = _nexo.LineaCotizacionDe(solicitud, Indice(12_000m));
        items.Select(i => i["lineaId"]!.GetValue<string>()).Should().Equal([fila1, fila1], "decisión 3: el primer camión es la fila 1 del cubicador");

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
        // Ruta 1 (28000 kg): el cubicador no puede escoger (extradimensionado); ruta 2 (12500 kg): condicionada.
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? clsCubicadorFalso.SinSeleccion("EXTRADIMENSIONADO")
            : clsCubicadorFalso.Condicionada(clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("2S2", "FURGON", 12_500m)), CodigoCafe);
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(dosRutas: true));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        (await CamionesAsync(folio)).Should().Equal("1|||28000.00|-", "2|2S2|FURGON|12500.00|R");
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Should().HaveCount(2, "la solicitud se envía completa a la Mesa de Costeo de Nexo");
        lineas.Select(l => (l["tipoVehiculo"]?.GetValue<string>(), l["peso"]!.GetValue<decimal>()))
            .Should().BeEquivalentTo([((string?)null, 28_000m), ("2S2", 12_500m)], "sin selección viaja sin vehículo; condicionada, como vino");
    }

    [Theory]
    [InlineData("MERCANCIA_DESCONOCIDA")]
    [InlineData("MERCANCIA_NO_CUBICABLE")]
    [InlineData("DIMENSIONES_INVALIDAS")]
    public async Task UnRechazoDelCubicadorLlegaAlComercialYNoSeGuardaNiSeEnviaNada(string codigo)
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, codigo);
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        envio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(envio, codigo);
        _nexo.UltimaAlta.Should().BeNull();
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    /// <summary>
    /// D-1019-1: sin el catálogo de mercancías la captura no valida el código (lo decide el cubicador); su rechazo llega con la ruta y la
    /// mercancía (<c>detalle.ruta</c> y <c>detalle.item</c>, base 0, y <c>detalle.campo</c>) para marcarla en el formulario.
    /// </summary>
    [Fact]
    public async Task SinCatalogoElRechazoDeUnaMercanciaLlegaConLaRutaYElItem()
    {
        _cubicador.TiposMercancia.Clear();
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, "MERCANCIA_DESCONOCIDA");
        _cubicador.CuerpoErrorRecomendacion =
            $$"""{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"'{{CodigoCafe}}' no esta en el catalogo de tipos de mercancia."}""";
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        envio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var detalle = JsonNode.Parse(await envio.Content.ReadAsStringAsync(Ct))!["error"]!["detalle"]!;
        detalle["ruta"]!.GetValue<int>().Should().Be(0);
        detalle["item"]!.GetValue<int>().Should().Be(0);
        detalle["campo"]!.GetValue<string>().Should().Be("codigo_mercancia");
        _nexo.UltimaAlta.Should().BeNull();
    }

    [Fact]
    public async Task UnaRespuestaIncompletaDelCubicadorSeRechazaAunqueHayaRespaldoYNoSeGuardaNada()
    {
        // HU-023.1: ASIGNADO con un vehículo sin peso es el fallo silencioso; nunca se tapa con el respaldo.
        _cubicador.CuerpoRecomendacion = """{"resultado":"ASIGNADO","vehiculos":[{"idRecomendacionVehiculo":"5f0e1d2c-3b4a-4968-8776-655443322110","codigoVehiculo":"3S3","codigoCarroceria":"FURGON","volumenTotalM3":10,"porcentajeLlenadoVolumen":50,"porcentajeLlenadoPeso":50}]}""";
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "CUBICADOR_RESPUESTA_INVALIDA");
        (await envio.Content.ReadAsStringAsync(Ct)).Should().NotContain("pesoTotalKg", "el detalle interno solo va al log");
        _nexo.UltimaAlta.Should().BeNull();
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
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

        var api = Api(new Dictionary<string, string?> { ["Cubicador:TiempoEsperaSegundos"] = "1", ["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1" });
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

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "el catálogo se corta en 1 s");
        (await CamionesAsync(folio)).Should().Equal(["1|3S3|FURGON|28000.00|R"],
            "HU-023.3: sin catálogo el codigo_mercancia se acepta tal cual y el cubicador lo valida al recomendar");
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

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(soloId: true));

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
        var cafe = tipos.EnumerateArray().Last();
        cafe.GetProperty("codigo").GetString().Should().Be(CodigoCafe);
        cafe.GetProperty("id").GetString().Should().Be(TipoCafe);
        cafe.GetProperty("categoria").GetString().Should().Be("GENERAL");
        cafe.GetProperty("cubicable").GetBoolean().Should().BeTrue();
        cafe.GetProperty("condicionada_cubicacion").GetBoolean().Should().BeFalse();
        cafe.GetProperty("activo").GetBoolean().Should().BeTrue();
        await VerificarContratoAsync(sinCubicador, "/web/tipos-mercancia", _get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(sinCubicador, "CUBICADOR_NO_DISPONIBLE");
    }

    [Fact]
    public async Task LosCatalogosDeCarroceriasYVehiculosSalenDelCubicador()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var carrocerias = await api.EnviarAsync(_get, "/web/tipos-carroceria", ana.Access);
        using var vehiculos = await api.EnviarAsync(_get, "/web/tipos-vehiculo", ana.Access);

        (await VerificarContratoAsync(carrocerias, "/web/tipos-carroceria", _get, HttpStatusCode.OK)).EnumerateArray()
            .Select(c => c.GetProperty("codigo").GetString()).Should().BeEquivalentTo("FURGON", "ESTACAS", "CAMA_BAJA");
        var vehiculo = (await VerificarContratoAsync(vehiculos, "/web/tipos-vehiculo", _get, HttpStatusCode.OK)).EnumerateArray()
            .Single(v => v.GetProperty("codigo").GetString() == "2S2");
        vehiculo.GetProperty("nombre").GetString().Should().Be("Tractocamión 2S2", "el nombre del vehículo sale del catálogo (decisión 4)");
        vehiculo.GetProperty("carrocerias").EnumerateArray().Select(c => c.GetString()).Should().Contain("FURGON");

        _cubicador.SinTiposVehiculo = true;
        using var sinPublicar = await Api().EnviarAsync(_get, "/web/tipos-vehiculo", ana.Access);
        await VerificarContratoAsync(sinPublicar, "/web/tipos-vehiculo", _get, HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Escenario E2E de la cadena HU-021 integrada (#132 a #137, #133, #136 y #140) con el Nexo falso y el cubicador falso: dos rutas (la 1
    /// con tres recomendaciones y un conexo capturado; la 2 con el respaldo), envío por camión con la línea de negocio de cada ruta, polling
    /// con emparejamiento, conexo diferido a la fila 1, margen en la ruta 2, PDF de pliego, re-solicitud -vN con los camiones en orden y
    /// los conexos con el id del catálogo, resultado ganada y métricas.
    /// </summary>
    [Fact]
    public async Task LaCadenaCompletaConElCubicadorLlegaDeLaCapturaALaVersionNuevaGanadaYSusMetricas()
    {
        // 1. Captura: la ruta 1 (28000 kg) recibe tres vehículos; para la 2 el cubicador no puede escoger (SUPERA_K_MAX: un camión sin
        // vehículo con el peso de la ruta, que resuelve la Mesa de Costeo).
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 12_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 9_000m),
                clsCubicadorFalso.Vehiculo("2", "FURGON", 7_000m))
            : clsCubicadorFalso.SinSeleccion("SUPERA_K_MAX");
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(dosRutas: true, conexo: true));
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        string[] camionesEsperados = ["1|3S3|FURGON|12000.00|R", "1|2S3|ESTACAS|9000.00|R", "1|2|FURGON|7000.00|R", "2|||12500.00|-"];
        (await CamionesAsync(folio)).Should().Equal(camionesEsperados);

        // 2. Envío por camión: una línea por camión, en orden, con la lineaNegocio de Nexo de su ruta.
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => (l["tipoVehiculo"]?.GetValue<string>(), l["peso"]!.GetValue<decimal>(), l["lineaNegocio"]!.GetValue<string>()))
            .Should().Equal(("3S3", 12_000m, "PuertoAInterior"), ("2S3", 9_000m, "PuertoAInterior"), ("2", 7_000m, "PuertoAInterior"),
                ((string?)null, 12_500m, "InteriorAPuerto"));

        // 3. Polling con las líneas barajadas: cada camión recibe el flete de SU línea.
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.SemillaOrdenLineas = 20261009;
        await CostearPorPesoAsync(api, folio, solicitud, lineas);
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(CAST(v.pesoKg AS nvarchar(20)), '=', CAST(v.costoFlete AS nvarchar(20))), ',') WITHIN GROUP (ORDER BY v.pesoKg) "
            + DeLosCamiones(folio)))
            .Should().Be("7000.00=500000.00,9000.00=800000.00,12000.00=1000000.00,12500.00=600000.00");

        // 4. Conexo diferido (decisión 10): el capturado llega una vez a la línea del primer camión vigente, la fila 1 del cubicador.
        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue);
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, Indice(lineas, 12_000m)));

        // 5. Margen solo en la ruta 2.
        var rutas = await RutasAsync(api, ana, folio);
        var precioRuta1 = rutas[0].GetProperty("precio_final_linea").GetDecimal();
        using var margen = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.30,"linea_orden":2}""", await EtagAsync(api, ana, folio));
        var ajustado = (await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK))
            .GetProperty("rutas").EnumerateArray().OrderBy(r => r.GetProperty("linea_orden").GetInt32()).ToList();
        ajustado[0].GetProperty("precio_final_linea").GetDecimal().Should().Be(precioRuta1, "la ruta 1 no se toca");
        ajustado[1].GetProperty("margen_aplicado").GetDecimal().Should().Be(0.30m);
        ajustado[1].GetProperty("precio_final_linea").GetDecimal().Should().Be(857_143m, "600.000 / (1 − 0,30)");

        // 6. PDF de pliego: dos rutas de dos líneas de negocio.
        using var pdf = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", ana.Access);
        await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        using var descarga = await api.EnviarAsync(_get, $"/pdfs/{folio}.pdf", ana.Access);
        descarga.StatusCode.Should().Be(HttpStatusCode.OK);
        var texto = clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(Ct)).Texto;
        // La cabecera "Varias" es el payload del pliego (sin texto propio en el documento); cada sección lleva la línea de su ruta.
        texto.Should().Contain("licitación (2 rutas)").And.Contain("RUTA 1: PLIEGO DE TRANSPORTE IMPORTACION")
            .And.Contain("RUTA 2: PLIEGO DE TRANSPORTE EXPORTACION").And.Contain("1 × Tractocamión 3S3 - Furgón", "HU-023.1: el PDF muestra los nombres del catálogo del cubicador, en el orden del cubicador")
            .And.NotContain("3S3 - FURGON");

        // 7. La Mesa entrega y el comercial pide una versión nueva: conserva los tres camiones en orden y el respaldo, y los conexos viajan
        // con el id del catálogo a la línea del primer camión.
        _nexo.Entregar(solicitud);
        await api.ProcesarPollingAsync(folio);
        using var reSolicitud = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            clsApiWebPruebas.Json(new { motivo = "Actualizar el costo", nueva_version = true }), await EtagAsync(api, ana, folio));
        var folioNuevo = (await VerificarContratoAsync(reSolicitud, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, HttpStatusCode.Created))
            .GetProperty("folio").GetString()!;
        folioNuevo.Should().Be(folio + "-v2");
        (await CamionesAsync(folioNuevo)).Should().Equal(camionesEsperados);
        var nueva = _nexo.SolicitudesCreadas[^1];
        nueva.Should().NotBe(solicitud);
        var lineasNuevas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineasNuevas.Select(l => l["peso"]!.GetValue<decimal>()).Should().Equal(12_000m, 9_000m, 7_000m, 12_500m);
        await CostearPorPesoAsync(api, folioNuevo, nueva, lineasNuevas);
        var itemNuevo = _nexo.PeticionesConexos(_nexo.CotizacionDe(nueva)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        itemNuevo["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue, "el id del catálogo de Nexo");
        itemNuevo["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(nueva, Indice(lineasNuevas, 12_000m)));

        // 8. Ganada por /resultado: motivo "No aplica" (#140).
        _nexo.Entregar(nueva);
        await api.ProcesarPollingAsync(folioNuevo);
        using var resultado = await api.EnviarAsync(_post, $"/web/cotizaciones/{folioNuevo}/resultado", ana.Access, """{"resultado":"ganada"}""");
        var marcada = await VerificarContratoAsync(resultado, "/web/cotizaciones/{folio}/resultado", _post, HttpStatusCode.OK);
        marcada.GetProperty("ganada").GetBoolean().Should().BeTrue();
        marcada.GetProperty("motivo_rechazo").GetString().Should().Be("No aplica");

        // 9. Métricas: una cadena (dos versiones) ganada; valores de la vigente, por ruta y línea de negocio.
        var rutasNuevas = await RutasAsync(api, ana, folioNuevo);
        var precio1 = rutasNuevas[0].GetProperty("precio_final_linea").GetDecimal();
        var precio2 = rutasNuevas[1].GetProperty("precio_final_linea").GetDecimal();
        precio2.Should().Be(857_143m, "la -vN conserva el margen negociado de la ruta 2");
        using var respuesta = await api.EnviarAsync(_get, "/web/metricas/comercial?desde=2026-01-01&hasta=2026-12-31", ana.Access);
        var metricas = await VerificarContratoAsync(respuesta, "/web/metricas/comercial", _get, HttpStatusCode.OK);
        var indicadores = metricas.GetProperty("indicadores");
        indicadores.GetProperty("cantidad").GetInt32().Should().Be(1, "la -v2 es la misma cadena");
        indicadores.GetProperty("versiones").GetInt32().Should().Be(2);
        indicadores.GetProperty("ganadas").GetInt32().Should().Be(1);
        indicadores.GetProperty("costeo_total").GetDecimal().Should().Be(3_050_000m, "2.300.000 de fletes + 150.000 del conexo en la ruta 1 y 600.000 en la ruta 2");
        indicadores.GetProperty("costeo_ganadas").GetDecimal().Should().Be(3_050_000m);
        indicadores.GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio1 + precio2);
        var porLinea = metricas.GetProperty("por_linea_negocio").EnumerateArray().ToDictionary(l => l.GetProperty("linea_negocio").GetString()!, l => l);
        porLinea.Keys.Should().BeEquivalentTo(["Importacion", "Exportacion"]);
        porLinea["Importacion"].GetProperty("rutas").GetInt32().Should().Be(1, "tres camiones son una ruta");
        porLinea["Importacion"].GetProperty("costeo_total").GetDecimal().Should().Be(2_450_000m);
        porLinea["Importacion"].GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio1);
        porLinea["Exportacion"].GetProperty("costeo_total").GetDecimal().Should().Be(600_000m);
        porLinea["Exportacion"].GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio2);
        porLinea["Exportacion"].GetProperty("margen_ponderado").GetDecimal().Should().Be(0.3m, "(857.143 − 600.000) / 857.143");
    }

    [Fact]
    public async Task LaSolicitudGuardaLaFlotaMixtaConVolumenPorcentajesYResultadoYDevuelveLosAvisos()
    {
        // HU-023.3 (#158): ruta 1 con flota mixta y una advertencia; ruta 2 sin selección (extradimensionada) y condicionada.
        _cubicador.Recomendar = p =>
        {
            if (clsCubicadorFalso.PesoTotal(p) == 28_000m)
            {
                var asignado = clsCubicadorFalso.Asignado(
                    clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m), clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 12_000m, 18m, 60m, 75m));
                asignado["advertencias"] = new[] { new { codigo = "REFRIGERADO", mensaje = "Confirmar el equipo de frío." } };
                return asignado;
            }

            var sinSeleccion = clsCubicadorFalso.Condicionada(clsCubicadorFalso.SinSeleccion("EXTRADIMENSIONADO"), CodigoCafe);
            sinSeleccion["extradimension"] = new
            {
                hayExtradimension = true,
                medidasEstandar = new { largoMaxCm = 1220, anchoMaxCm = 260, altoMaxCm = 300, pesoMaxKg = 34000 },
                piezas = new[] { new { indicePosicion = 0, idTipoMercancia = (string?)null, codigoMercancia = CodigoCafe, excesos = new[] { "alto 320.0 cm > 300.0 cm" } } },
            };
            return sinSeleccion;
        };
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario(dosRutas: true));
        var creada = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        var folio = creada.GetProperty("folio").GetString()!;

        // La respuesta trae, por ruta, el resultado y los avisos (que no se guardan).
        var cubicacion = creada.GetProperty("cubicacion").EnumerateArray().ToList();
        cubicacion.Select(c => (c.GetProperty("linea_orden").GetInt32(), c.GetProperty("resultado").GetString()))
            .Should().Equal((1, "ASIGNADO"), (2, "SIN_SELECCION_AUTOMATICA"));
        cubicacion[0].GetProperty("advertencias")[0].GetProperty("codigo").GetString().Should().Be("REFRIGERADO");
        cubicacion[1].GetProperty("motivo_sin_seleccion").GetString().Should().Be("EXTRADIMENSIONADO");
        cubicacion[1].GetProperty("condicionada").GetBoolean().Should().BeTrue();
        cubicacion[1].GetProperty("mercancias_condicionadas")[0].GetProperty("codigo_mercancia").GetString().Should().Be(CodigoCafe);
        cubicacion[1].GetProperty("extradimension").GetProperty("hay_extradimension").GetBoolean().Should().BeTrue();
        cubicacion[1].GetProperty("extradimension").GetProperty("piezas")[0].GetProperty("indice_posicion").GetInt32().Should().Be(0);

        // En la base: la ruta con su corrida, resultado, motivo y marca; cada camión con su volumen y porcentajes; la mercancía con su código.
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(l.orden, '|', IIF(l.idRecomendacion IS NULL, '-', 'R'), '|', l.resultadoCubicacion, '|', ISNULL(l.motivoSinSeleccion, '-'), '|', "
            + "CAST(l.condicionada AS int)), ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("1|R|ASIGNADO|-|0,2|R|SIN_SELECCION_AUTOMATICA|EXTRADIMENSIONADO|1");
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(l.orden, '|', ISNULL(v.tipoVehiculo, '-'), '|', CAST(v.volumenTotalM3 AS nvarchar(20)), '|', "
            + "CAST(v.porcentajeLlenadoVolumen AS nvarchar(20)), '|', CAST(v.porcentajeLlenadoPeso AS nvarchar(20))), ',') "
            + $"WITHIN GROUP (ORDER BY l.orden, v.createdAt) {DeLosCamiones(folio)}"))
            .Should().Be("1|3S3|62.500|81.25|47.06,1|C2|18.000|60.00|75.00,2|-|||", "sin selección: un camión sin vehículo ni datos de llenado");
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(m.codigoMercancia, ',') FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be($"{CodigoCafe},{CodigoCafe}");

        // GET .../costeo devuelve el resultado por ruta y el volumen y los porcentajes por camión.
        var rutas = await RutasAsync(api, ana, folio);
        rutas[0].GetProperty("resultado_cubicacion").GetString().Should().Be("ASIGNADO");
        rutas[0].GetProperty("camiones")[0].GetProperty("porcentaje_llenado_volumen").GetDecimal().Should().Be(81.25m);
        rutas[0].GetProperty("mercancias")[0].GetProperty("codigo_mercancia").GetString().Should().Be(CodigoCafe);
        rutas[1].GetProperty("motivo_sin_seleccion").GetString().Should().Be("EXTRADIMENSIONADO");
        rutas[1].GetProperty("condicionada").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SiFallaLaTerceraRutaNoSeGuardaNingunaNiSeEnviaNadaANexo()
    {
        // Todo o nada (HU-023.3): tres rutas; el cubicador responde algo incompleto en la tercera.
        var llamadas = 0;
        _cubicador.Recomendar = _ => Interlocked.Increment(ref llamadas) < 3
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 10_000m))
            : new Dictionary<string, object?> { ["resultado"] = "ASIGNADO", ["vehiculos"] = new[] { new { codigoVehiculo = "3S3" } } };
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = JsonNode.Parse(Formulario(dosRutas: true))!;
        formulario["rutas"]!.AsArray().Add(formulario["rutas"]![1]!.DeepClone());

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, formulario.ToJsonString());

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "CUBICADOR_RESPUESTA_INVALIDA");
        _cubicador.Recomendaciones.Should().HaveCount(3, "las corridas de las dos primeras rutas quedan huérfanas en el cubicador (se acepta)");
        _nexo.UltimaAlta.Should().BeNull();
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task RecalcularUnaRutaAntesDeEnviarANexoReemplazaSusCamionesYDespuesDelEnvioPideUnaVersionNueva()
    {
        // La solicitud queda en error_envio (Nexo rechaza el alta): los camiones todavía no están en Nexo.
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 28_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var rutaId = (await RutasAsync(api, ana, folio))[0].GetProperty("linea_solicitud_id").GetString();
        var ruta = $"/web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos";

        // El cubicador ahora recomienda dos camiones: la ruta se recalcula con su mismo id y los reemplaza.
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("2S2", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 12_000m));
        using var sinIfMatch = await ConVersionAsync(api, _post, ruta, ana.Access, "{}", null);
        sinIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        using var recalculo = await ConVersionAsync(api, _post, ruta, ana.Access, "{}", await EtagAsync(api, ana, folio));
        var costeo = await VerificarContratoAsync(recalculo, "/web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos", _post, HttpStatusCode.OK);

        costeo.GetProperty("rutas")[0].GetProperty("camiones").EnumerateArray().Select(c => c.GetProperty("tipo_vehiculo").GetString())
            .Should().Equal("2S2", "C2");
        costeo.GetProperty("rutas")[0].GetProperty("avisos_cubicacion").GetProperty("advertencias").GetArrayLength()
            .Should().Be(0, "el recálculo devuelve los avisos de su llamada (no se guardan)");
        (await CamionesAsync(folio)).Should().Equal("1|2S2|FURGON|16000.00|R", "1|C2|ESTACAS|12000.00|R");
        _cubicador.Recomendaciones.Select(r => r["idLineaSolicitud"]!.GetValue<string>()).Should().OnlyContain(id => id == rutaId);

        // Una vez en Nexo, los camiones ya no se cambian aquí: versión nueva (re-solicitar).
        _nexo.AltaFalla = null;
        using var reintento = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);
        _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!["tipoVehiculo"]!.GetValue<string>()).Should().Equal("2S2", "C2");
        using var tarde = await ConVersionAsync(api, _post, ruta, ana.Access, "{}", await EtagAsync(api, ana, folio));
        tarde.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(tarde, "ESTADO_NO_PERMITE");
    }

    [Fact]
    public async Task RecalcularMientrasElReintentoEnviaANexoNoPisaLosCamionesNiPierdeLaSolicitudDeNexo()
    {
        // Regresión del validador (HU-023.3): el alta del reintento queda retenida en Nexo y en ese momento se recalcula la ruta.
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 28_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var rutaId = (await RutasAsync(api, ana, folio))[0].GetProperty("linea_solicitud_id").GetString();
        var etagAntes = await EtagAsync(api, ana, folio);
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("2S2", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 12_000m));
        _nexo.AltaFalla = null;
        HttpStatusCode? durante = null;
        HttpStatusCode? duranteConEtagNuevo = null;
        _nexo.AntesDelAlta = async () =>
        {
            _nexo.AntesDelAlta = null;
            var ruta = $"/web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos";
            using var conEtagViejo = await ConVersionAsync(api, _post, ruta, ana.Access, "{}", etagAntes);
            durante = conEtagViejo.StatusCode;
            using var conEtagNuevo = await ConVersionAsync(api, _post, ruta, ana.Access, "{}", await EtagAsync(api, ana, folio));
            duranteConEtagNuevo = conEtagNuevo.StatusCode;
        };

        using var reintento = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);

        reintento.StatusCode.Should().Be(HttpStatusCode.OK);
        durante.Should().Be(HttpStatusCode.Conflict, "el reclamo del reenvío subió la versión: el If-Match anterior ya no vale");
        duranteConEtagNuevo.Should().Be(HttpStatusCode.Conflict, "con el envío en curso no se cambian los camiones (ESTADO_NO_PERMITE)");
        (await CamionesAsync(folio)).Should().Equal(["1|3S3|FURGON|28000.00|R"], "Nexo y la base tienen los mismos camiones");
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            $"SELECT solicitudNexoId FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'")).Should().Be(_nexo.SolicitudesCreadas[0]);
    }

    /// <summary>
    /// E2E de la LICITACIÓN (HU-021.12, #139) con el Nexo falso y el cubicador falso, hasta el PDF: un pliego Excel de tres filas (la 1 con
    /// mercancías y un conexo → dos camiones del cubicador; la 2 con mercancías → uno; la 3 sin mercancías → respaldo sin vehículo), una sola
    /// solicitud de Nexo con una línea por camión, polling parcial y completo con las líneas barajadas, conexo diferido a la fila 1, margen
    /// en la ruta 3, PDF de pliego sin costos de Nexo, Excel exportado con el precio de cada ruta, "Mis cotizaciones", entrega y ganada.
    /// </summary>
    [Fact]
    public async Task LaLicitacionConElCubicadorLlegaDelExcelAlPdfDePliegoYAlResultado()
    {
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) switch
        {
            28_000m => clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 12_000m)),
            _ => clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("2", "FURGON", 12_500m)),
        };
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object MercanciaPliego(int cantidad, decimal peso) => new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Estiba de café", cantidad, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = peso };
        object[] filas =
        [
            new { fila = 0, origen = "BUENAVENTURA", origen_municipio_id = 76109, destino = "BOGOTA, D.C.", destino_municipio_id = 11001, tipologia = "CargaSeca", peso_kg = 28_000m,
                valor_mercancia = 50_000_000m, mercancias = new[] { MercanciaPliego(20, 1400m) }, servicios_conexos = new[] { new { id_servicio_conexo = Cargue, cantidad = 1 } } },
            new { fila = 1, origen = "CALI", origen_municipio_id = 76001, destino = "CARTAGENA", destino_municipio_id = 13001, tipologia = "CargaSeca", peso_kg = 12_500m,
                valor_mercancia = 50_000_000m, mercancias = new[] { MercanciaPliego(10, 1250m) } },
            new { fila = 2, origen = "CUCUTA", origen_municipio_id = 54001, destino = "BOGOTA, D.C.", destino_municipio_id = 11001, tipologia = "CargaSeca", peso_kg = 8_000m,
                valor_mercancia = 50_000_000m },
        ];

        // 1. Carga del pliego (Excel + filas mapeadas).
        using var alta = await api.EnviarContenidoAsync(_post, "/web/licitaciones", ana.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, filas), clsPliegoMuestra.Excel()));
        var creada = await VerificarContratoAsync(alta, "/web/licitaciones", _post, HttpStatusCode.Created);
        var folio = creada.GetProperty("folio").GetString()!;
        // HU-023.5 (#160): con el cubicador la licitación se guarda sin esperarlo y se cubica en segundo plano antes de ir a Nexo.
        creada.GetProperty("estado_costeo").GetString().Should().Be("pendiente_envio");
        creada.GetProperty("progreso_cubicacion").GetProperty("estado").GetString().Should().Be("en_curso");
        _nexo.UltimaAlta.Should().BeNull();
        await api.ProcesarCubicacionAsync(folio);

        // 2. Cubicador por fila: dos camiones, uno y el respaldo; cada ruta con su fila de origen del Excel.
        (await CamionesAsync(folio)).Should().Equal("1|3S3|FURGON|16000.00|R", "1|2S3|ESTACAS|12000.00|R", "2|2|FURGON|12500.00|R", "3|||8000.00|-");
        _cubicador.Recomendaciones.Should().HaveCount(2, "la fila sin mercancías no llama al cubicador");
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(l.orden, '=', l.rutaOrigenIndice), ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND c.tipoSolicitud = 'licitacion'"))
            .Should().Be("1=0,2=1,3=2");

        // 3. Una solicitud de Nexo de tipo licitación con una línea por camión, en orden.
        _nexo.UltimaAlta!["tipo"]!.GetValue<string>().Should().Be("licitacion");
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => (l["tipoVehiculo"]?.GetValue<string>(), l["peso"]!.GetValue<decimal>()))
            .Should().Equal(("3S3", 16_000m), ("2S3", 12_000m), ("2", 12_500m), ((string?)null, 8_000m));

        // 4. Polling parcial (falta un camión de la ruta 1) y completo, con las líneas barajadas.
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.SemillaOrdenLineas = 20261010;
        _nexo.CostearLinea(solicitud, Indice(lineas, 16_000m), 1_000_000m);
        _nexo.CostearLinea(solicitud, Indice(lineas, 12_500m), 700_000m);
        _nexo.CostearLinea(solicitud, Indice(lineas, 8_000m), 400_000m);
        await api.ProcesarPollingAsync(folio);
        using (var parcial = await api.EnviarAsync(_get, $"/web/licitaciones/{folio}", ana.Access))
        {
            var vista = await VerificarContratoAsync(parcial, "/web/licitaciones/{folio}", _get, HttpStatusCode.OK);
            vista.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo", "a la ruta 1 le falta un camión");
            vista.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        }

        _nexo.CostearLinea(solicitud, Indice(lineas, 12_000m), 800_000m);
        await api.ProcesarPollingAsync(folio);
        var rutas = await RutasAsync(api, ana, folio);
        // (1.800.000 de fletes + 150.000 del conexo) / 0,85; 700.000 / 0,85; 400.000 / 0,85 (política de Nacional).
        rutas.Select(r => r.GetProperty("precio_final_linea").GetDecimal()).Should().Equal(2_294_118m, 823_529m, 470_588m);
        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, Indice(lineas, 16_000m)), "el conexo de la ruta va al primer camión");

        // 5. Margen solo en la ruta 3.
        using (var margen = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.20,"linea_orden":3,"justificacion":"Retorno asegurado"}""", await EtagAsync(api, ana, folio)))
        {
            var ajustado = (await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK))
                .GetProperty("rutas").EnumerateArray().OrderBy(r => r.GetProperty("linea_orden").GetInt32()).ToList();
            ajustado.Select(r => r.GetProperty("precio_final_linea").GetDecimal()).Should().Equal(2_294_118m, 823_529m, 500_000m);
        }

        // 6. PDF de pliego: una sección por ruta, los camiones de la ruta 1 en orden y ningún costo de Nexo.
        using var pdf = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", ana.Access);
        var pdfJson = await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        using var descarga = await api.EnviarAsync(_get, new Uri(new Uri("http://localhost"), pdfJson.GetProperty("pdf_url").GetString()!).AbsolutePath, ana.Access);
        descarga.StatusCode.Should().Be(HttpStatusCode.OK);
        var texto = clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(Ct)).Texto;
        // Tarifa por viaje de cada ruta SIN conexos (1.800.000 / 0,85) y el conexo aparte con su precio (150.000 / 0,85).
        texto.Should().Contain("RESUMEN DEL PLIEGO").And.Contain("$2.117.647").And.Contain("$823.529").And.Contain("$500.000")
            .And.Contain("TOTAL DEL PLIEGO (3 RUTAS, SIN SERVICIOS CONEXOS) $3.441.176").And.Contain("Cargue en puerto: $176.471");
        texto.Should().Contain("1 × Tractocamión 3S3 - Furgón, 1", "los dos camiones de la ruta 1 en el orden del cubicador, con los nombres del catálogo (HU-023.1)")
            .And.Contain("× Tractocamión 2S3 - Estacas (28000 KG)");
        foreach (var costo in new[] { "$1.000.000", "$800.000", "$700.000", "$400.000", "$1.950.000" })
        {
            texto.Should().NotContain(costo, "los costos de Nexo nunca van al PDF");
        }

        // 7. Excel exportado: las columnas del cliente y la tarifa por viaje de cada ruta (sin conexos, igual que el PDF).
        using (var excel = await api.EnviarAsync(_get, $"/web/licitaciones/{folio}/excel", ana.Access))
        {
            excel.StatusCode.Should().Be(HttpStatusCode.OK);
            using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync(Ct)));
            var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
            hoja.Cell(clsPliegoMuestra.FilaExcel(0), 2).GetString().Should().Be("BUENAVENTURA");
            Enumerable.Range(0, 3).Select(i => hoja.Cell(clsPliegoMuestra.FilaExcel(i), 7).GetValue<decimal>()).Should().Equal(2_117_647m, 823_529m, 500_000m);
        }

        // 8. "Mis cotizaciones" la muestra con el total y la línea de negocio de la ruta 1.
        using (var lista = await api.EnviarAsync(_get, "/web/mis-cotizaciones", ana.Access))
        {
            var json = await VerificarContratoAsync(lista, "/web/mis-cotizaciones", _get, HttpStatusCode.OK);
            var fila = json.GetProperty("cotizaciones").EnumerateArray()
                .Should().ContainSingle(c => c.GetProperty("folio").GetString() == folio).Subject;
            fila.GetProperty("precio_final").GetDecimal().Should().Be(3_617_647m);
            fila.GetProperty("linea_negocio").GetString().Should().Be("Nacional");
        }

        // 9. La Mesa entrega y el comercial la marca ganada.
        _nexo.Entregar(solicitud);
        await api.ProcesarPollingAsync(folio);
        using var resultado = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", ana.Access, """{"resultado":"ganada"}""");
        var marcada = await VerificarContratoAsync(resultado, "/web/cotizaciones/{folio}/resultado", _post, HttpStatusCode.OK);
        marcada.GetProperty("ganada").GetBoolean().Should().BeTrue();
        _nexo.Resultado(solicitud).Should().Contain("anada", "el resultado viaja a Nexo antes de guardarse");
    }

    private static int Indice(IReadOnlyList<JsonNode> lineas, decimal peso) => lineas.ToList().FindIndex(l => l["peso"]!.GetValue<decimal>() == peso);

    /// <summary>La Mesa costea cada línea por su peso (12000 → 1.000.000, 9000 → 800.000, 7000 → 500.000, 12500 → 600.000) y corre el polling.</summary>
    private async Task CostearPorPesoAsync(clsApiWebPruebas api, string folio, string solicitud, IReadOnlyList<JsonNode> lineas)
    {
        foreach (var (peso, flete) in new[] { (12_000m, 1_000_000m), (9_000m, 800_000m), (7_000m, 500_000m), (12_500m, 600_000m) })
        {
            _nexo.CostearLinea(solicitud, Indice(lineas, peso), flete);
        }

        await api.ProcesarPollingAsync(folio);
    }

    private static async Task<List<JsonElement>> RutasAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return [.. json.GetProperty("rutas").EnumerateArray().OrderBy(r => r.GetProperty("linea_orden").GetInt32())];
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

    /// <summary>
    /// API con el cubicador falso. Estas pruebas verifican la cadena completa con los códigos del cubicador en Nexo, así que encienden
    /// <c>Nexo:EnviarCodigosCubicador</c> (apagada por defecto, contrato de Nexo #297) salvo que <paramref name="extra"/> diga otra cosa.
    /// </summary>
    private clsApiWebPruebas Api(IDictionary<string, string?>? extra = null)
    {
        var configuracion = new Dictionary<string, string?> { ["Nexo:EnviarCodigosCubicador"] = "true" };
        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            configuracion[clave] = valor;
        }

        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, extra: configuracion, cubicador: _cubicador);
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

    /// <summary>
    /// Una mercancía con <c>codigo_mercancia</c> (HU-023.3) o, con <paramref name="soloId"/>, solo con el <c>id_tipo_mercancia</c> obsoleto
    /// (transición: se traduce al código con el catálogo).
    /// </summary>
    private static object Mercancia(bool soloId, string nombre, int cantidad, int pesoKg) => soloId
        ? new { id_tipo_mercancia = TipoCafe, nombre_mercancia = nombre, cantidad, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = pesoKg }
        : new { codigo_mercancia = CodigoCafe, nombre_mercancia = nombre, cantidad, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = pesoKg };

    /// <summary>Formulario por rutas: la ruta 1 con 20 estibas de 1400 kg (28000 kg) y, con <paramref name="dosRutas"/>, una ruta 2 de 12500 kg.</summary>
    private static string Formulario(bool dosRutas = false, bool conexo = false, bool soloId = false)
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
            mercancias = new object[] { Mercancia(soloId, "Estiba de café", 20, 1400) },
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
            mercancias = new object[] { Mercancia(soloId, "Café verde", 10, 1250) },
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
