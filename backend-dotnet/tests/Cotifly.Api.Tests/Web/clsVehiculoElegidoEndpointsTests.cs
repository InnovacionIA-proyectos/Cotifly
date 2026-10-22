using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-032 (#197, ADR-022): el vehículo elegido como respaldo del cubicador por HTTP con SQL Server real (script 12), el Nexo falso y el
/// cubicador falso: captura de una ruta sin mercancías cubicables, con mercancías sin medidas y con "sin selección"; la edición que solo
/// cambia el vehículo; la re-solicitud con <c>datos_carga.tipo_vehiculo</c>; y la licitación con filas sin medidas, que no esperan al
/// cubicador.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsVehiculoElegidoEndpointsTests : IDisposable
{
    private const string Captura = "/web/solicitudes-costeo";
    private const string Costeo = "/web/cotizaciones/{folio}/costeo";
    private const string Edicion = "/web/cotizaciones/{folio}/solicitud";
    private const string ReSolicitud = "/web/cotizaciones/{folio}/costeo/re-solicitar";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsVehiculoElegidoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add((Guid.Parse(clsCosteoEndpointsTests.TipoMercancia), "Café en estibas"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Formulario de una ruta de Cupo con el vehículo y las mercancías dados.</summary>
    private static string Formulario(string? vehiculo, decimal? peso, params object[] mercancias)
    {
        var ruta = new JsonObject
        {
            ["origen_municipio_id"] = 76109, ["origen"] = "Buenaventura", ["destino_municipio_id"] = 11001, ["destino"] = "Bogotá D.C.",
            ["tipologia"] = "CargaSeca", ["clase_viaje"] = "Cupo", ["valor_mercancia"] = 50_000_000m,
        };
        if (vehiculo is not null)
        {
            ruta["tipo_vehiculo"] = vehiculo;
        }

        if (peso is not null)
        {
            ruta["peso_kg"] = peso.Value;
        }

        if (mercancias.Length > 0)
        {
            ruta["mercancias"] = JsonSerializer.SerializeToNode(mercancias);
        }

        return new JsonObject
        {
            ["tipo"] = "spot", ["nit_cliente"] = clsCosteoEndpointsTests.Nit, ["cliente_nombre"] = "Longyin", ["linea_negocio"] = "Importacion",
            ["rutas"] = new JsonArray(ruta),
        }.ToJsonString();
    }

    private static object Cubicable(decimal peso) =>
        new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Café", cantidad = 1, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = peso };

    [Fact]
    public async Task UnaRutaSoloConPesoNoVaAlCubicadorLlevaElElegidoYElGetDeLaSolicitudLoDevuelve()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2s3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        _cubicador.Recomendaciones.Should().BeEmpty();
        var linea = _nexo.UltimaAlta!["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        (linea["tipoVehiculo"]!.GetValue<string>(), linea["peso"]!.GetValue<decimal>()).Should().Be(("2S3", 9_000m));
        (await ElegidoAsync(folio)).Should().Be("2S3|", "se guarda escrito como en Nexo");
        using var solicitud = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/solicitud", ana.Access);
        var editable = await VerificarContratoAsync(solicitud, Edicion, _get, HttpStatusCode.OK);
        editable.GetProperty("rutas")[0].GetProperty("tipo_vehiculo").GetString().Should().Be("2S3");
    }

    [Fact]
    public async Task SinVehiculoEnCupoEs400VehiculoRequeridoYNadaSeGuarda()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario(null, 9_000m));

        envio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await envio.Content.ReadAsStringAsync(Ct));
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("detalle").GetProperty("motivo").GetString().Should().Be("VEHICULO_REQUERIDO");
        var porRuta = error.GetProperty("detalle").GetProperty("errores").EnumerateArray().Should().ContainSingle().Subject;
        (porRuta.GetProperty("ruta").GetInt32(), porRuta.GetProperty("campo").GetString()).Should().Be((0, "tipo_vehiculo"));
        _nexo.UltimaAlta.Should().BeNull();
    }

    [Fact]
    public async Task LasMercanciasSinMedidasSeGuardanEnNullNoViajanComoPosicionesYElCosteoLasMuestraSinMedidas()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access,
            Formulario("3S3", null, new { nombre_mercancia = "Bultos", cantidad = 4, peso_kg = 250 }, new { nombre_mercancia = "Cajas", cantidad = 1, largo_cm = (decimal?)null, peso_kg = 1000 }));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        _cubicador.Recomendaciones.Should().BeEmpty();
        var linea = _nexo.UltimaAlta!["lineas"]![0]!;
        linea["posiciones"].Should().BeNull();
        linea["peso"]!.GetValue<decimal>().Should().Be(2_000m, "a Nexo va el peso de la ruta: la suma de las mercancías");
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var mercancias = (await VerificarContratoAsync(costeo, Costeo, _get, HttpStatusCode.OK)).GetProperty("rutas")[0].GetProperty("mercancias");
        mercancias.EnumerateArray().Should().HaveCount(2).And.OnlyContain(m => m.GetProperty("largo_cm").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task SinSeleccionVaElElegidoConSuAvisoEnLaRespuestaYEnElCosteo()
    {
        _cubicador.Recomendar = _ => clsCubicadorFalso.SinSeleccion("SIN_CARROCERIA_COMPATIBLE");
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("3S3", null, Cubicable(1_000m)));
        var respuesta = await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created);
        var folio = respuesta.GetProperty("folio").GetString()!;

        _cubicador.Recomendaciones.Should().ContainSingle();
        var aviso = respuesta.GetProperty("cubicacion")[0].GetProperty("advertencias").EnumerateArray()
            .Should().ContainSingle(a => a.GetProperty("codigo").GetString() == "VEHICULO_ELEGIDO_USADO").Subject;
        aviso.GetProperty("vehiculo_elegido").GetProperty("tipo_vehiculo").GetString().Should().Be("3S3");
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        (await VerificarContratoAsync(costeo, Costeo, _get, HttpStatusCode.OK)).GetProperty("rutas")[0].GetProperty("avisos_cubicacion")
            .GetProperty("advertencias").EnumerateArray().Select(a => a.GetProperty("codigo").GetString()).Should().Contain("VEHICULO_ELEGIDO_USADO");
    }

    [Fact]
    public async Task EditarSoloElVehiculoDeUnaRutaSinMercanciasCubicablesEsUnCambioQueViajaANexo()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;

        // El mismo cuerpo del GET (con el vehículo elegido) no es un cambio.
        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        using var igual = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);
        (await VerificarContratoAsync(igual, Edicion, _put, HttpStatusCode.OK)).GetProperty("sin_cambios").GetBoolean().Should().BeTrue();

        (modelo, etag) = await ModeloAsync(api, ana, folio);
        modelo["rutas"]![0]!["tipo_vehiculo"] = "3S3";
        using var cambio = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        var editada = await VerificarContratoAsync(cambio, Edicion, _put, HttpStatusCode.OK);
        editada.GetProperty("sin_cambios").GetBoolean().Should().BeFalse();
        editada.GetProperty("rutas_replanificadas").EnumerateArray().Select(r => r.GetInt32()).Should().Equal(1);
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");
        (await ElegidoAsync(folio)).Should().Be("3S3|");
    }

    [Fact]
    public async Task LaReSolicitudConElVehiculoEnDatosDeCargaLoCambiaEnLaVersionNueva()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        using var leido = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var etag = leido.Headers.ETag!.Tag;

        using var respuesta = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"El cliente pidió otro vehículo","datos_carga":{"tipo_vehiculo":"3S3"}}""", etag);

        var nueva = (await VerificarContratoAsync(respuesta, ReSolicitud, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        (await ElegidoAsync(nueva)).Should().Be("3S3|");
        (await CamionAsync(nueva)).Should().Be("3S3");
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");
    }

    [Fact]
    public async Task LaLicitacionConFilasSinMedidasNoEsperaAlCubicadorEnEsasFilas()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 1_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object[] filas =
        [
            new { fila = 0, origen = "BUENAVENTURA", origen_municipio_id = 76109, destino = "BOGOTA, D.C.", destino_municipio_id = 11001, tipologia = "CargaSeca",
                valor_mercancia = 50_000_000m, tipo_vehiculo = "2S3", mercancias = new object[] { Cubicable(1_000m) } },
            new { fila = 1, origen = "CALI", origen_municipio_id = 76001, destino = "CARTAGENA", destino_municipio_id = 13001, tipologia = "CargaSeca",
                valor_mercancia = 50_000_000m, tipo_vehiculo = "2", mercancias = new object[] { new { nombre_mercancia = "Bultos", cantidad = 8, peso_kg = 500 } } },
        ];

        using var alta = await api.EnviarContenidoAsync(_post, "/web/licitaciones", ana.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, filas), clsPliegoMuestra.Excel(2)));
        var folio = (await VerificarContratoAsync(alta, "/web/licitaciones", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarCubicacionAsync(folio);

        _cubicador.Recomendaciones.Should().ContainSingle("solo la fila con mercancías cubicables va al cubicador");
        (await CamionesAsync(folio)).Should().Equal("1|3S3", "2|2");
        _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!["tipoVehiculo"]!.GetValue<string>()).Should().Equal("3S3", "2");
        using var leida = await api.EnviarAsync(_get, $"/web/licitaciones/{folio}", ana.Access);
        var cubicacion = (await VerificarContratoAsync(leida, "/web/licitaciones/{folio}", _get, HttpStatusCode.OK)).GetProperty("cubicacion");
        cubicacion.EnumerateArray().Should().ContainSingle(r => r.GetProperty("linea_orden").GetInt32() == 1)
            .Which.GetProperty("advertencias").EnumerateArray().Select(a => a.GetProperty("codigo").GetString())
            .Should().Contain("VEHICULO_AJUSTADO_POR_CUBICADOR");
    }

    // --- Validación de HU-032 (validador/auditor): bordes por HTTP ---

    [Fact]
    public async Task ConPosicionesCubicablesUnElegidoQueNexoNoConoceNoBloqueaYSiElCubicadorAsignaMandaElCubicador()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 1_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("NO-EXISTE", null, Cubicable(1_000m)));

        var respuesta = await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created);
        var aviso = respuesta.GetProperty("cubicacion")[0].GetProperty("advertencias").EnumerateArray()
            .Should().ContainSingle(a => a.GetProperty("codigo").GetString() == "VEHICULO_AJUSTADO_POR_CUBICADOR").Subject;
        aviso.GetProperty("vehiculo_elegido").GetProperty("tipo_vehiculo").GetString().Should().Be("NO-EXISTE");
        aviso.GetProperty("vehiculos_finales")[0].GetProperty("tipo_vehiculo").GetString().Should().Be("3S3");
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");
    }

    [Fact]
    public async Task ConElCubicadorCaidoUnElegidoQueNexoNoConoceDejaLaRutaSinVehiculoConSuAvisoYLaCapturaNoSePierde()
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.ServiceUnavailable, "CAIDO");
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("NO-EXISTE", null, Cubicable(1_000m)));

        var respuesta = await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created);
        var folio = respuesta.GetProperty("folio").GetString()!;
        respuesta.GetProperty("cubicacion")[0].GetProperty("advertencias").EnumerateArray().Select(a => a.GetProperty("codigo").GetString())
            .Should().Equal("VEHICULO_ELEGIDO_INVALIDO");
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"].Should().BeNull("el elegido no es de Nexo: lo resuelve la Mesa");
        (await ElegidoAsync(folio)).Should().Be("NO-EXISTE|", "el elegido se conserva en la ruta aunque no viaje");
    }

    [Fact]
    public async Task UnaMercanciaConCodigoYSinMedidasEs400EnLargoCm()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access,
            Formulario("3S3", null, new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Café", cantidad = 1, peso_kg = 1000 }));

        envio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await envio.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("errores").EnumerateArray()
            .Should().Contain(e => e.GetProperty("campo").GetString() == "mercancias[0].largo_cm" && e.GetProperty("codigo").GetString() == "POSICION_INVALIDA");
        _cubicador.Recomendaciones.Should().BeEmpty();
        _nexo.UltimaAlta.Should().BeNull();
    }

    [Fact]
    public async Task UnaRutaMixtaSinVehiculoEs400YConVehiculoVaSinPosicionesConSuAviso()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object[] mercancias = [Cubicable(1_000m), new { nombre_mercancia = "Bultos", cantidad = 2, peso_kg = 500 }];

        using var sinVehiculo = await api.EnviarAsync(_post, Captura, ana.Access, Formulario(null, null, mercancias));
        sinVehiculo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sinVehiculo.Content.ReadAsStringAsync(Ct)).Should().Contain("VEHICULO_REQUERIDO");

        using var conVehiculo = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", null, mercancias));
        var respuesta = await VerificarContratoAsync(conVehiculo, Captura, _post, HttpStatusCode.Created);
        _cubicador.Recomendaciones.Should().BeEmpty("una ruta mixta no se cubica");
        respuesta.GetProperty("cubicacion")[0].GetProperty("resultado").ValueKind.Should().Be(JsonValueKind.Null);
        respuesta.GetProperty("cubicacion")[0].GetProperty("advertencias").EnumerateArray().Select(a => a.GetProperty("codigo").GetString())
            .Should().Equal("MERCANCIAS_NO_CUBICABLES");
        var linea = _nexo.UltimaAlta!["lineas"]![0]!;
        (linea["posiciones"], linea["peso"]!.GetValue<decimal>(), linea["tipoVehiculo"]!.GetValue<string>()).Should().Be((null, 2_000m, "2S3"));
    }

    [Fact]
    public async Task EnContenedorSinVehiculoNiMercanciasSeCapturaConUnCamionSinVehiculo()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = JsonNode.Parse(Formulario(null, 9_000m))!;
        cuerpo["rutas"]![0]!["clase_viaje"] = "Contenedor";
        cuerpo["rutas"]![0]!["clase_contenedor"] = "CNT40.HC";
        cuerpo["rutas"]![0]!["tipologia"] = "Contenedor";

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, cuerpo.ToJsonString());

        await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created);
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"].Should().BeNull("en Contenedor Nexo deduce el vehículo");
    }

    [Fact]
    public async Task ConLaBanderaApagadaUnaRutaSoloConPesoYSinVehiculoSeCapturaComoAntesYElElegidoSeIgnora()
    {
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, new Dictionary<string, string?> { ["Captura:AceptarVehiculoCliente"] = "false" },
            cubicador: _cubicador);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var sinVehiculo = await api.EnviarAsync(_post, Captura, ana.Access, Formulario(null, 9_000m));
        await VerificarContratoAsync(sinVehiculo, Captura, _post, HttpStatusCode.Created);
        using var conVehiculo = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(conVehiculo, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"].Should().BeNull("con la bandera apagada el elegido se ignora (decisión 7)");
        (await ElegidoAsync(folio)).Should().Be("|");
    }

    [Fact]
    public async Task EditarSoloElVehiculoDeUnaRutaQueAsignoElCubicadorEsSinCambios()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 1_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", null, Cubicable(1_000m)));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;
        var recomendaciones = _cubicador.Recomendaciones.Count;

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        modelo["rutas"]![0]!["tipo_vehiculo"].Should().NotBeNull();
        modelo["rutas"]![0]!["tipo_vehiculo"]!.GetValue<string>().Should().Be("2S3", "el GET devuelve el elegido, no el del cubicador");
        modelo["rutas"]![0]!["tipo_vehiculo"] = "2";
        using var cambio = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        (await VerificarContratoAsync(cambio, Edicion, _put, HttpStatusCode.OK)).GetProperty("sin_cambios").GetBoolean()
            .Should().BeTrue("en una ruta ASIGNADO el elegido es solo el respaldo (decisión 6)");
        _cubicador.Recomendaciones.Should().HaveCount(recomendaciones, "no se vuelve a cubicar");
    }

    [Fact]
    public async Task EditarUnaRutaCubicadaAgregandoUnaMercanciaSinMedidasLaReplanificaConElElegidoYReemplazaLosAvisos()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 1_000m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", null, Cubicable(1_000m)));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        modelo["rutas"]![0]!["mercancias"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { nombre_mercancia = "Bultos", cantidad = 1, peso_kg = 500 }));
        modelo["rutas"]![0]!["peso_kg"] = null;
        using var cambio = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        var editada = await VerificarContratoAsync(cambio, Edicion, _put, HttpStatusCode.OK);
        editada.GetProperty("sin_cambios").GetBoolean().Should().BeFalse();
        var advertencias = editada.GetProperty("cotizacion").GetProperty("cubicacion")[0].GetProperty("advertencias").EnumerateArray()
            .Select(a => a.GetProperty("codigo").GetString()).ToList();
        advertencias.Should().Equal(["MERCANCIAS_NO_CUBICABLES"], "el aviso del ajuste anterior se reemplaza al replanificar");
        (await CamionAsync(folio)).Should().Be("2S3");
        _nexo.UltimaAlta!["lineas"]![0]!["posiciones"].Should().BeNull();
    }

    [Fact]
    public async Task LaReSolicitudConDatosDeCargaSinVehiculoConservaElElegidoDeUnaRutaSinMercanciasCubicables()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        using var leido = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var etag = leido.Headers.ETag!.Tag;

        using var respuesta = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"Cambió el peso","datos_carga":{"peso_kg":9500}}""", etag);

        var nueva = (await VerificarContratoAsync(respuesta, ReSolicitud, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        (await ElegidoAsync(nueva)).Should().Be("2S3|");
        (await CamionAsync(nueva)).Should().Be("2S3");
    }

    [Fact]
    public async Task LaReSolicitudSinDatosDeCargaCopiaElElegido()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        using var leido = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);

        using var respuesta = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"Otra versión","nueva_version":true}""", leido.Headers.ETag!.Tag);

        var nueva = (await VerificarContratoAsync(respuesta, ReSolicitud, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        (await ElegidoAsync(nueva)).Should().Be("2S3|");
    }

    [Fact]
    public async Task EditarUnFolioAnteriorSoloConPesoYSinVehiculoReenviandoElMismoCuerpoEsSinCambios()
    {
        // B3 (decisión del usuario del 2026-10-21): una ruta que no cambia no exige el vehículo. Una ruta de Cupo solo con peso y sin vehículo
        // era válida antes de HU-032 (se simula una ruta anterior al 12: sin elegido y con su camión sin vehículo): el mismo cuerpo del GET
        // es "sin cambios", no VEHICULO_REQUERIDO.
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = await FolioAnteriorSinVehiculoAsync(api, ana, enviado: false);

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        modelo["rutas"]![0]!["tipo_vehiculo"].Should().BeNull();
        using var igual = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        (await VerificarContratoAsync(igual, Edicion, _put, HttpStatusCode.OK)).GetProperty("sin_cambios").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("9000.00")]
    [InlineData("9000.0")]
    public async Task EditarUnFolioAnteriorSinVehiculoConElMismoPesoEnOtraEscalaSigueSinCambios(string peso)
    {
        // "Cambia" se compara por valor, como sin_cambios (D-1019B-1): 9000.00 es el mismo peso que 9000.
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = await FolioAnteriorSinVehiculoAsync(api, ana, enviado: false);

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        modelo["rutas"]![0]!["peso_kg"] = JsonNode.Parse(peso);
        using var igual = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        (await VerificarContratoAsync(igual, Edicion, _put, HttpStatusCode.OK)).GetProperty("sin_cambios").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EditarUnFolioAnteriorSinVehiculoCambiandoElPesoDeLaRutaPideElVehiculo()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = await FolioAnteriorSinVehiculoAsync(api, ana, enviado: false);

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        var altas = _nexo.SolicitudesCreadas.Count;
        modelo["rutas"]![0]!["peso_kg"] = 9_500m;
        using var cambio = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        cambio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErroresVehiculoRequeridoAsync(cambio)).Should().Equal(0);
        _nexo.SolicitudesCreadas.Should().HaveCount(altas, "nada se envía");
    }

    [Fact]
    public async Task EditarQuitandoElVehiculoDeUnaRutaSoloConPesoSinTocarLaCargaPideElVehiculo()
    {
        // Auditoría B3: la exención de "ruta sin cambios" no se puede usar para quitar el vehículo elegido. Quitarlo es un cambio (los
        // camiones dependen de él), aunque la carga sea la misma y lo único que cambie además sea algo que no se cubica (el valor).
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        var altas = _nexo.SolicitudesCreadas.Count;
        modelo["rutas"]![0]!.AsObject().Remove("tipo_vehiculo");
        modelo["rutas"]![0]!["valor_mercancia"] = 60_000_000m;
        using var cambio = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        cambio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErroresVehiculoRequeridoAsync(cambio)).Should().Equal(0);
        _nexo.SolicitudesCreadas.Should().HaveCount(altas, "nada se envía");
        (await ElegidoAsync(folio)).Should().Be("2S3|");
    }

    [Fact]
    public async Task EditarUnFolioAnteriorSinVehiculoAgregandoUnaRutaNuevaSinVehiculoLaPideSoloEnLaNueva()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = await FolioAnteriorSinVehiculoAsync(api, ana, enviado: false);

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        var nueva = modelo["rutas"]![0]!.DeepClone();
        nueva.AsObject().Remove("linea_solicitud_id");
        nueva["destino_municipio_id"] = 76001;
        nueva["destino"] = "Cali";
        modelo["rutas"]!.AsArray().Add(nueva);
        using var agregada = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/solicitud", ana.Access, modelo.ToJsonString(), etag);

        agregada.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErroresVehiculoRequeridoAsync(agregada)).Should().Equal([1], "la ruta guardada no cambió: solo la nueva exige el vehículo");
    }

    [Fact]
    public async Task LaReSolicitudDeUnFolioAnteriorSinVehiculoPideElVehiculoSoloSiLaRutaCambia()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = await FolioAnteriorSinVehiculoAsync(api, ana, enviado: true);
        using var leido = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var etag = leido.Headers.ETag!.Tag;

        // Cambia el peso: la ruta cambia y, sin mercancías cubicables ni vehículo, es VEHICULO_REQUERIDO (nada se reserva ni se envía).
        var altas = _nexo.SolicitudesCreadas.Count;
        using var cambio = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"Cambió el peso","datos_carga":{"peso_kg":9500}}""", etag);
        cambio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await cambio.Content.ReadAsStringAsync(Ct)).Should().Contain("VEHICULO_REQUERIDO");
        _nexo.SolicitudesCreadas.Should().HaveCount(altas, "nada se envía");

        // Los mismos datos de carga (9000.0 es el peso guardado, por valor) no cambian la ruta: no exigen el vehículo.
        using var igual = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"Otra versión","datos_carga":{"peso_kg":9000.0}}""", etag);
        var nueva = (await VerificarContratoAsync(igual, ReSolicitud, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        (await ElegidoAsync(nueva)).Should().Be("|");
    }

    [Fact]
    public async Task LaReSolicitudConDatosDeCargaGuardaElVehiculoEscritoComoEnNexo()
    {
        // B4 (HU-032): como la captura, el vehículo validado se guarda con la escritura del catálogo de Nexo (Canonica).
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        using var leido = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);

        using var respuesta = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"Otro vehículo","datos_carga":{"tipo_vehiculo":"3s3","carroceria":"furgon"}}""", leido.Headers.ETag!.Tag);

        var nueva = (await VerificarContratoAsync(respuesta, ReSolicitud, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        (await ElegidoAsync(nueva)).Should().Be("3S3|FURGON");
        (await CamionAsync(nueva)).Should().Be("3S3");
    }

    [Fact]
    public async Task EnCargaSueltaSinVehiculoEs400PorDefectoYSeCapturaSiSeConfiguraComoExenta()
    {
        var cuerpo = JsonNode.Parse(Formulario(null, 9_000m))!;
        cuerpo["rutas"]![0]!["clase_viaje"] = "Carga Suelta";

        // Por defecto solo Contenedor está exento.
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var porDefecto = await api.EnviarAsync(_post, Captura, ana.Access, cuerpo.ToJsonString());
        porDefecto.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErroresVehiculoRequeridoAsync(porDefecto)).Should().Equal(0);

        // Con Captura:ClasesSinVehiculoObligatorio = [Contenedor, Carga Suelta] la ruta se captura sin vehículo (y Cupo lo sigue exigiendo).
        using var exenta = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, new Dictionary<string, string?>
        {
            ["Captura:ClasesSinVehiculoObligatorio:0"] = "Contenedor",
            ["Captura:ClasesSinVehiculoObligatorio:1"] = "Carga Suelta",
        }, cubicador: _cubicador);
        var beto = await exenta.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var capturada = await exenta.EnviarAsync(_post, Captura, beto.Access, cuerpo.ToJsonString());
        await VerificarContratoAsync(capturada, Captura, _post, HttpStatusCode.Created);
        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"].Should().BeNull();
        using var cupo = await exenta.EnviarAsync(_post, Captura, beto.Access, Formulario(null, 9_000m));
        cupo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErroresVehiculoRequeridoAsync(cupo)).Should().Equal(0);
    }

    /// <summary>
    /// Un folio "anterior a HU-032": una ruta de Cupo solo con peso, sin vehículo elegido y con su camión sin vehículo (se captura con el
    /// vehículo y se borra en la base). <paramref name="enviado"/>: costeado y vencido (para re-solicitar); si no, en <c>error_envio</c>.
    /// </summary>
    private async Task<string> FolioAnteriorSinVehiculoAsync(clsApiWebPruebas api, clsSesionPrueba sesion, bool enviado)
    {
        _nexo.AltaFalla = enviado ? null : HttpStatusCode.ServiceUnavailable;
        using var envio = await api.EnviarAsync(_post, Captura, sesion.Access, Formulario("2S3", 9_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;
        if (enviado)
        {
            await api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        }

        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE l SET tipoVehiculoElegido = NULL, carroceriaElegida = NULL FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'; "
            + "UPDATE v SET tipoVehiculo = NULL FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}';");
        return folio;
    }

    /// <summary>Las rutas (índice base 0) con <c>VEHICULO_REQUERIDO</c> en <c>tipo_vehiculo</c> de un 400 de la captura o la edición.</summary>
    private static async Task<IReadOnlyList<int>> ErroresVehiculoRequeridoAsync(HttpResponseMessage respuesta)
    {
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        return [.. json.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("errores").EnumerateArray()
            .Where(e => e.GetProperty("codigo").GetString() == "VEHICULO_REQUERIDO" && e.GetProperty("campo").GetString() == "tipo_vehiculo")
            .Select(e => e.GetProperty("ruta").GetInt32())];
    }

    [Fact]
    public async Task UnaLicitacionConUnaFilaSoloConPesoYSinVehiculoEs400VehiculoRequerido()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object[] filas =
        [
            new { fila = 0, origen = "CALI", origen_municipio_id = 76001, destino = "CARTAGENA", destino_municipio_id = 13001, tipologia = "CargaSeca",
                valor_mercancia = 50_000_000m, peso_kg = 9_000m },
        ];

        using var alta = await api.EnviarContenidoAsync(_post, "/web/licitaciones", ana.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, filas), clsPliegoMuestra.Excel(1)));

        alta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await alta.Content.ReadAsStringAsync(Ct)).Should().Contain("VEHICULO_REQUERIDO");
    }

    private async Task<(JsonNode Modelo, string Etag)> ModeloAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var get = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/solicitud", sesion.Access);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var modelo = JsonNode.Parse(await get.Content.ReadAsStringAsync(Ct))!;
        modelo["modo_esperado"] = modelo["edicion"]!["modo"]!.GetValue<string>();
        return (modelo, get.Headers.ETag!.Tag);
    }

    private Task<string?> ElegidoAsync(string folio) =>
        clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT CONCAT(l.tipoVehiculoElegido, '|', l.carroceriaElegida) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

    private Task<string?> CamionAsync(string folio) =>
        clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT TOP 1 v.tipoVehiculo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

    private async Task<IReadOnlyList<string>> CamionesAsync(string folio) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT STRING_AGG(CONCAT(l.orden, '|', v.tipoVehiculo), ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))!.Split(',');

    private clsApiWebPruebas Api()
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: _cubicador);
        _apis.Add(api);
        return api;
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
}
