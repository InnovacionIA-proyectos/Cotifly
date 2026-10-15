using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Costeo en Nexo por HTTP contra la base real y un Nexo falso (ADR-015, HU-005.5): el envío crea la cotización aunque Nexo falle,
/// el reintento reenvía lo guardado y las respuestas cumplen <c>specs/openapi.yaml</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCosteoEndpointsTests : IDisposable
{
    public const string Nit = "900555111";

    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsCosteoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Cuerpo <c>SolicitudCosteoWeb</c> completo (snake_case escrito a mano, como lo manda el frontend).</summary>
    /// <summary>Tipo de mercancía del catálogo del cubicador que llevan las mercancías del formulario de prueba.</summary>
    public const string TipoMercancia = "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50";

    /// <summary>HU-023.3: código del tipo de mercancía en el catálogo del cubicador (lo que manda el contrato nuevo).</summary>
    public const string CodigoMercancia = "CAFE_EN_ESTIBAS";

    /// <summary>
    /// Formulario por rutas (HU-021.6, <c>RutaCapturaWeb</c>): una ruta Buenaventura → Bogotá con los ids de municipio del Nexo falso y
    /// mercancías con su tipo. Sin vehículo ni carrocería (decisión 7); <c>valor_estimado</c> y <c>referencia</c> se aceptan y se ignoran.
    /// </summary>
    public static string Formulario(string nit = Nit, string linea = "Importacion") => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = nit,
        cliente_nombre = "Longyin",
        linea_negocio = linea,
        deadline = "2026-10-15T17:00:00-05:00",
        valor_estimado = 12000000,
        referencia = "OC-7788",
        horas_cargue = 4,
        horas_descargue = 6.5,
        rutas = new object[]
        {
            new
            {
                origen_municipio_id = 76109,
                origen = "Buenaventura",
                destino_municipio_id = 11001,
                destino = "Bogotá D.C.",
                tipologia = "Contenedor",
                clase_viaje = "Contenedor",
                clase_contenedor = "CNT40.HC",
                exclusivo = true,
                consolidado = false,
                retorna_contenedor = true,
                ciudad_devolucion_municipio_id = 76109,
                ciudad_devolucion_contenedor = "Buenaventura",
                levante_contenedor_diferente = false,
                peso_kg = 28500.5,
                valor_mercancia = 250000000,
                mercancias = new object[]
                {
                    new { codigo_mercancia = CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1424.95m },
                    new { codigo_mercancia = CodigoMercancia, largo_cm = 1200, ancho_cm = 235, alto_cm = 269, peso_kg = 1 },
                },
            },
        },
    });

    [Fact]
    public async Task ElEnvioCreaLaCotizacionLaMandaANexoYRespondeCotizacionResumenPendienteDeCosteo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);

        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        json.GetProperty("motivo_estado_costeo").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("nit_cliente").GetString().Should().Be(Nit);
        json.GetProperty("cliente").GetString().Should().Be("LONGYIN S.A.S.", "el nombre sale del catálogo de Nexo");
        json.GetProperty("linea_negocio").GetString().Should().Be("Importacion");
        json.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null, "el costeo todavía no pasó por el margen");
        json.GetProperty("folio").GetString().Should().StartWith($"PCN-{Nit}-");

        var alta = _nexo.UltimaAlta!;
        alta["clienteBp"]!.GetValue<string>().Should().Be("15927");
        alta["lineas"]![0]!["posiciones"]!.AsArray().Should().HaveCount(2);
        var peticion = _nexo.Peticiones.Last(p => p.Metodo == _post);
        peticion.IdPortalUsuario.Should().NotBeNullOrEmpty("Nexo identifica al comercial por X-Portal-clsUsuario-Id");

        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(solicitudNexoId, '|', estadoCosteo, '|', (SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias p JOIN dbo_cotifly.CotizacionLineasSolicitud lp ON lp.id = p.lineaSolicitudId WHERE lp.cotizacionSolicitudId = c.id)) FROM dbo_cotifly.CotizacionesSolicitud c WHERE folio = '{json.GetProperty("folio").GetString()}'");
        fila.Should().Be($"{_nexo.SolicitudesCreadas[^1]}|pendiente_costeo|2", "HU-021: el id de la solicitud de Nexo va en solicitudNexoId (0010)");
    }

    /// <summary>
    /// HU-018.1 (#155): "Ver detalle" muestra el <c>deadline</c> del formulario (cabecera) y el <c>scorePrioridad</c> que Nexo devolvió al
    /// crear la solicitud, en <c>GET .../costeo</c> y en <c>CotizacionResumen</c> (respuesta del envío y "Mis cotizaciones").
    /// </summary>
    [Fact]
    public async Task HU0181_ElCosteoYElResumenTraenElDeadlineYElScorePrioridadDeNexo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.ScorePrioridadAlta = 62.5m;
        var deadline = new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var resumen = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        var folio = resumen.GetProperty("folio").GetString()!;
        resumen.GetProperty("deadline").GetDateTimeOffset().Should().Be(deadline, "el formulario manda 2026-10-15T17:00:00-05:00");
        resumen.GetProperty("score_prioridad").GetDecimal().Should().Be(62.5m);

        using var costeo = await _api.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("deadline").GetDateTimeOffset().Should().Be(deadline);
        json.GetProperty("score_prioridad").GetDecimal().Should().Be(62.5m);
        json.GetProperty("rutas")[0].TryGetProperty("deadline", out _).Should().BeFalse("el deadline es de la solicitud (cabecera), no de la ruta");

        using var lista = await _api.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", ana.Access);
        var cotizaciones = (await VerificarContratoAsync(lista, "/web/mis-cotizaciones", HttpMethod.Get, HttpStatusCode.OK)).GetProperty("cotizaciones");
        var fila = cotizaciones.EnumerateArray().Single(c => c.GetProperty("folio").GetString() == folio);
        fila.GetProperty("deadline").GetDateTimeOffset().Should().Be(deadline);
        fila.GetProperty("score_prioridad").GetDecimal().Should().Be(62.5m);

        (await clsBaseDatosPruebas.EscalarAsync<decimal?>(_entorno.BaseDatos.CadenaCotifly,
                $"SELECT scorePrioridad FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be(62.5m, "se guarda en la cabecera (script 09)");
    }

    [Fact]
    public async Task HU0181_SinScorePrioridadDeNexoNiDeadlineLosDosSalenEnNull()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.ScorePrioridadAlta = null;
        var formulario = System.Text.Json.Nodes.JsonNode.Parse(Formulario())!.AsObject();
        formulario.Remove("deadline").Should().BeTrue();
        var cuerpo = formulario.ToJsonString();

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var costeo = await _api.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("deadline").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("score_prioridad").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ConNexoCaidoLaCotizacionQuedaGuardadaEnErrorEnvioYElReintentoLaEnvia()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var json = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        json.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("motivo_estado_costeo").GetString().Should().Contain("HTTP 503");
        var folio = json.GetProperty("folio").GetString()!;

        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);
        var reenviada = await VerificarContratoAsync(reintento, "/web/cotizaciones/{folio}/costeo/reintentar", _post, HttpStatusCode.OK);

        reenviada.GetProperty("folio").GetString().Should().Be(folio);
        reenviada.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        reenviada.GetProperty("motivo_estado_costeo").ValueKind.Should().Be(JsonValueKind.Null);
        _nexo.UltimaAlta!["lineas"]![0]!["origen"]!.GetValue<string>().Should().Be("Buenaventura", "se reenvía lo guardado");
    }

    [Fact]
    public async Task Un400DeNexoEsUnErrorDefinitivoQueNoSeReintenta()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.BadRequest;

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var json = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);

        json.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("motivo_estado_costeo").GetString().Should().StartWith("Rechazo definitivo de Nexo").And.Contain("HTTP 400 VALIDATION_ERROR");

        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{json.GetProperty("folio").GetString()}/costeo/reintentar", ana.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.Conflict, "los mismos datos volverían a fallar");
        await VerificarErrorNegocioAsync(reintento, "COSTEO_NO_REINTENTABLE");
    }

    [Fact]
    public async Task ReintentarUnaCotizacionYaEnviadaEs409YUnaAjenaEs404()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var propia = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);
        using var ajena = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", luis.Access);

        propia.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(propia, "COSTEO_NO_REINTENTABLE");
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ajena.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Cotización no encontrada o no es tuya."}""");
    }

    [Theory]
    [InlineData("""{"tipo":"urgente","nit_cliente":"900555111","linea_negocio":"Importacion","origen":"Cali","destino":"Bogotá"}""", "SOLICITUD_COSTEO_INVALIDA")]
    [InlineData("""{"tipo":"spot","linea_negocio":"Importacion","origen":"Cali","destino":"Bogotá"}""", "SOLICITUD_COSTEO_INVALIDA")]
    [InlineData("""{"tipo":"spot","nit_cliente":"900555111","linea_negocio":"Importacion","origen":"Cali","destino":"Bogotá","posiciones":[{"largo_cm":0,"ancho_cm":1,"alto_cm":1}]}""", "SOLICITUD_COSTEO_INVALIDA")]
    [InlineData("""{"tipo":"spot","nit_cliente":"900555111","linea_negocio":"Marítima","origen":"Cali","destino":"Bogotá","peso_kg":1000}""", "MARGEN_LINEA_DESCONOCIDA")]
    [InlineData("""{"tipo":"spot","nit_cliente":"900555111","linea_negocio":"Importacion","origen":"Cali","destino":"Bogotá","peso_kg":1000,"posiciones":[{"cantidad":2,"largo_cm":1,"ancho_cm":1,"alto_cm":1,"peso_kg":400}]}""", "SOLICITUD_COSTEO_INVALIDA")]
    public async Task UnFormularioInvalidoEs400ConErrorNegocioYNoSeGuardaNada(string cuerpo, string codigo)
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        int Altas() => _nexo.Peticiones.Count(p => p.Metodo == _post);
        var antes = Altas();

        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, codigo);
        Altas().Should().Be(antes, "sin formulario válido no se envía nada a Nexo (HU-021.6: solo se consultan sus catálogos para validar)");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task SinNexoConfiguradoUnFormularioSinCatalogosDeNexoQuedaEnErrorEnvio()
    {
        var ana = await _entorno.ApiSinCorreo.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        // HU-021.6 (decisión 2): aun con el id y el nombre, el nombre se copia del catálogo: sin Nexo no hay captura.
        var cuerpo = """{"tipo":"spot","nit_cliente":"900555111","linea_negocio":"Importacion","origen":"Cali","origen_municipio_id":76001,"destino":"Bogotá","destino_municipio_id":11001,"peso_kg":1000}""";

        using var envio = await _entorno.ApiSinCorreo.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "MUNICIPIOS_NO_DISPONIBLE");
    }

    [Fact]
    public async Task ConRutasLosCamposPlanosEnLaRaizSonErroresYNoSeDescartan()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario().Replace("\"rutas\":", "\"ciudad_devolucion_contenedor\":\"Cali\",\"peso_kg\":1,\"rutas\":", StringComparison.Ordinal);

        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "SOLICITUD_COSTEO_INVALIDA");
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("errores").EnumerateArray()
            .Select(e => (e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString()))
            .Should().BeEquivalentTo([("ciudad_devolucion_contenedor", "CAMPO_NO_APLICA"), ("peso_kg", "CAMPO_NO_APLICA")]);
    }

    [Fact]
    public async Task SinNexoConfiguradoUnMunicipioSinIdNoSePuedeResolverYNoSeGuarda()
    {
        // HU-021.6 (decisión 2): el id es obligatorio; sin catálogo de municipios no se adivina ni se guarda texto suelto.
        var ana = await _entorno.ApiSinCorreo.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = """{"tipo":"spot","nit_cliente":"900555111","linea_negocio":"Importacion","origen":"Cali","destino":"Bogotá","peso_kg":1000}""";

        using var envio = await _entorno.ApiSinCorreo.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "MUNICIPIOS_NO_DISPONIBLE");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task SinNexoConfiguradoUnaClaseDeViajeNoSePuedeValidarYNoSeGuarda()
    {
        var ana = await _entorno.ApiSinCorreo.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await _entorno.ApiSinCorreo.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "CATALOGOS_FLETE_NO_DISPONIBLE");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task UnaClaseDeViajeFueraDelCatalogoMaestroEs400ConTodosLosErroresYNoLlegaANexo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario()
            .Replace("\"clase_viaje\":\"Contenedor\"", "\"clase_viaje\":\"Barco\"", StringComparison.Ordinal)
            .Replace("\"destino_municipio_id\":11001,\"destino\":\"Bogot\\u00E1 D.C.\"", "\"destino\":\"La Uni\\u00F3n\"", StringComparison.Ordinal);
        cuerpo.Should().Contain("La Uni", "el reemplazo del destino debe aplicarse (System.Text.Json escapa las tildes)");

        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "SOLICITUD_COSTEO_INVALIDA");
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        var errores = json.RootElement.GetProperty("error").GetProperty("detalle").GetProperty("errores").EnumerateArray()
            .Select(e => (e.GetProperty("ruta").GetInt32(), e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString())).ToList();
        errores.Should().BeEquivalentTo([(0, "clase_contenedor", "CAMPO_NO_APLICA"), (0, "clase_viaje", "CODIGO_NO_CATALOGO"), (0, "destino", "MUNICIPIO_AMBIGUO")],
            "HU-021.6: todos los errores de la captura llegan juntos y el municipio ambiguo nunca se adivina");
        _nexo.Peticiones.Should().NotContain(p => p.Metodo == _post, "nunca se envía texto libre a Nexo");
    }

    [Fact]
    public async Task ElVehiculoYLaCarroceriaQueLleganSeIgnoranYElCamionVaSinVehiculo()
    {
        // HU-021 (decisión 7): un cliente con el contrato anterior que todavía manda vehículo y carrocería no falla.
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario().Replace("\"tipologia\":", "\"tipo_vehiculo\":\"3S3\",\"carroceria\":\"FURGON\",\"tipologia\":", StringComparison.Ordinal);

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"].Should().BeNull("la Mesa de Costeo de Nexo define el vehículo");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND v.tipoVehiculo IS NULL AND v.pesoKg = 28500.5"))
            .Should().Be(1, "un camión sin vehículo con el peso total de la ruta");
    }

    [Fact]
    public async Task ConLaBanderaDeRespaldoElVehiculoDelClienteVaEnElCamionDelRespaldo()
    {
        // HU-021.11 (#138): Captura:AceptarVehiculoCliente (HU-021-FE.2) mientras no hay cubicador. El vehículo se valida contra el
        // catálogo de Nexo y va en el único camión del respaldo; con la bandera apagada (por defecto) se ignora (prueba anterior).
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, new Dictionary<string, string?> { ["Captura:AceptarVehiculoCliente"] = "true" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario().Replace("\"tipologia\":", "\"tipo_vehiculo\":\"3S3\",\"carroceria\":\"FURGON\",\"tipologia\":", StringComparison.Ordinal);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        _nexo.UltimaAlta!["lineas"]![0]!["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");
        _nexo.UltimaAlta!["lineas"]![0]!["carroceria"]!.GetValue<string>().Should().Be("FURGON");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND v.tipoVehiculo = '3S3' AND v.carroceria = 'FURGON' AND v.pesoKg = 28500.5"))
            .Should().Be(1, "un solo camión, el del respaldo, con el vehículo del cliente y el peso total de la ruta");
    }

    [Fact]
    public async Task ConLaBanderaDeRespaldoUnVehiculoFueraDelCatalogoEsCodigoNoCatalogo()
    {
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, new Dictionary<string, string?> { ["Captura:AceptarVehiculoCliente"] = "true" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario().Replace("\"tipologia\":", "\"tipo_vehiculo\":\"Tractomula\",\"tipologia\":", StringComparison.Ordinal);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        envio.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(envio, "SOLICITUD_COSTEO_INVALIDA");
        _nexo.Peticiones.Should().NotContain(p => p.Metodo == _post, "nunca se envía texto libre a Nexo");
    }

    [Fact]
    public async Task ElRequestANexoLlevaNotasMunicipiosHorasPorLineaYElDeadlineEnHoraDeBogota()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario()
            .Replace("\"deadline\":\"2026-10-15T17:00:00-05:00\"", "\"deadline\":\"2026-10-15\",\"notas\":\"Cita previa en puerto\"", StringComparison.Ordinal);

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);
        var json = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        var folio = json.GetProperty("folio").GetString()!;

        var alta = _nexo.UltimaAlta!;
        alta["deadline"]!.GetValue<string>().Should().Be("2026-10-15T00:00:00-05:00", "una fecha sin zona es de Bogotá, no UTC");
        alta["notas"]!.GetValue<string>().Should().Be("Cita previa en puerto");
        var linea = alta["lineas"]![0]!;
        linea["origenMunicipioId"]!.GetValue<int>().Should().Be(76109);
        linea["destinoMunicipioId"]!.GetValue<int>().Should().Be(11001);
        linea["horasCargue"]!.GetValue<decimal>().Should().Be(4m);
        linea["horasDescargue"]!.GetValue<decimal>().Should().Be(6.5m);
        linea["tipoVehiculo"].Should().BeNull("HU-021: el comercial ya no digita el vehículo (decisión 7)");

        (await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT CONCAT(c.notas, '|', l.origenMunicipioId, '|', l.destinoMunicipioId) FROM dbo_cotifly.CotizacionesSolicitud c "
            + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id WHERE c.folio = '{folio}'"))
            .Should().Be("Cita previa en puerto|76109|11001", "el reintento reenvía lo guardado");
    }

    [Fact]
    public async Task ElPrimerEnvioYElReintentoMandanLaMismaSolicitudNormalizada()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        var cuerpo = Formulario().Replace("\"CNT40.HC\"", "\" CNT40.HC \"", StringComparison.Ordinal).Replace("\"OC-7788\"", "\"  OC-7788 \"", StringComparison.Ordinal);

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);

        var altas = _nexo.Peticiones.Where(p => p.Metodo == _post && p.Url.AbsolutePath == "/api/captura-solicitudes").Select(p => p.Cuerpo!).ToList();
        altas.Should().HaveCount(2);
        // Mismo contenido; solo puede cambiar la escala de los decimales (12000000 en memoria, 12000000.00 al releer DECIMAL(18,2)).
        Canonico(altas[0]).Should().Be(Canonico(altas[1]), "el primer envío y el reintento mandan lo guardado");
        using var primero = JsonDocument.Parse(altas[0]);
        primero.RootElement.GetProperty("lineas")[0].GetProperty("claseContenedor").GetString().Should().Be("CNT40.HC");
        primero.RootElement.TryGetProperty("referencia", out _).Should().BeFalse("HU-021 (decisión 1): la referencia se acepta y se ignora");
    }

    [Fact]
    public async Task UnRechazoDeCatalogoDeNexoQuedaComoErrorDefinitivoConElCodigoDeNexo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.BadRequest;
        _nexo.CodigoAltaFalla = "ENUM_INVALIDO";

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var json = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);

        json.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("motivo_estado_costeo").GetString().Should().StartWith("Rechazo definitivo de Nexo").And.Contain("ENUM_INVALIDO");
    }

    /// <summary>JSON con los números sin ceros decimales a la derecha, para comparar contenido y no la escala de un decimal.</summary>
    private static string Canonico(string json)
    {
        static System.Text.Json.Nodes.JsonNode? Normalizar(System.Text.Json.Nodes.JsonNode? nodo) => nodo switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o.Select(p =>
                new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(p.Key, Normalizar(p.Value)))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(Normalizar)]),
            System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
                System.Text.Json.Nodes.JsonValue.Create(v.GetValue<decimal>() / 1.000000000000000000000000000000000m),
            _ => nodo?.DeepClone(),
        };

        return Normalizar(System.Text.Json.Nodes.JsonNode.Parse(json))!.ToJsonString();
    }

    /// <summary>
    /// #173: el E2E real recibió un 500 DESPUÉS de guardar y enviar a Nexo; un reintento del frontend duplicaba la solicitud. Con la misma
    /// <c>Idempotency-Key</c>, el segundo POST devuelve la misma cotización sin crear otro folio ni otra solicitud en Nexo; sin clave, cada
    /// POST es una captura nueva.
    /// </summary>
    [Fact]
    public async Task ConLaMismaIdempotencyKeyElReintentoDeLaCapturaNoDuplicaLaSolicitudEnNexo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();
        var antes = _nexo.SolicitudesCreadas.Count;

        using var primera = await CapturarConClaveAsync(ana.Access, clave, Formulario());
        var folio = (await VerificarContratoAsync(primera, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString();
        using var reintento = await CapturarConClaveAsync(ana.Access, clave, Formulario());
        var repetida = await VerificarContratoAsync(reintento, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);

        repetida.GetProperty("folio").GetString().Should().Be(folio, "la repetición devuelve la cotización de la primera");
        _nexo.SolicitudesCreadas.Count.Should().Be(antes + 1, "una sola solicitud en Nexo");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = (SELECT usuarioId FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}')"))
            .Should().Be(1, "un solo folio");

        using var otroCuerpo = await CapturarConClaveAsync(ana.Access, clave, Formulario(linea: "Nacional"));
        otroCuerpo.StatusCode.Should().Be(HttpStatusCode.BadRequest, "la misma clave con otro cuerpo es SOLICITUD_INVALIDA");

        using var sinClave = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        (await VerificarContratoAsync(sinClave, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()
            .Should().NotBe(folio, "sin clave cada POST es una captura nueva");
        _nexo.SolicitudesCreadas.Count.Should().Be(antes + 2);
    }

    private async Task<HttpResponseMessage> CapturarConClaveAsync(string token, string clave, string cuerpo)
    {
        using var peticion = new HttpRequestMessage(_post, "/web/solicitudes-costeo");
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
