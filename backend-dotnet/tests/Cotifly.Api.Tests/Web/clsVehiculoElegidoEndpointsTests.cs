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
