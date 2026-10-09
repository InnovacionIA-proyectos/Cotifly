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
        origen = "Buenaventura",
        destino = "Bogotá D.C.",
        tipo_vehiculo = "3S3",
        carroceria = "PORTACONTENEDORES",
        tipologia = "Contenedor",
        clase_viaje = "Contenedor",
        clase_contenedor = "CNT40.HC",
        exclusivo = true,
        consolidado = false,
        retorna_contenedor = true,
        ciudad_devolucion_contenedor = "Buenaventura",
        levante_contenedor_diferente = false,
        ciudad_levante_contenedor = (string?)null,
        valor_aprovisionamiento = (decimal?)null,
        peso_kg = 28500.5,
        valor_mercancia = 250000000,
        posiciones = new object[]
        {
            new { descripcion = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1425 },
            new { largo_cm = 1200, ancho_cm = 235, alto_cm = 269 },
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
        peticion.IdPortalUsuario.Should().NotBeNullOrEmpty("Nexo identifica al comercial por X-Portal-Usuario-Id");

        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(solicitudNexoId, '|', estadoCosteo, '|', (SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias p JOIN dbo_cotifly.CotizacionLineasSolicitud lp ON lp.id = p.lineaSolicitudId WHERE lp.cotizacionSolicitudId = c.id)) FROM dbo_cotifly.CotizacionesSolicitud c WHERE folio = '{json.GetProperty("folio").GetString()}'");
        fila.Should().Be($"{_nexo.SolicitudesCreadas[^1]}|pendiente_costeo|2", "HU-021: el id de la solicitud de Nexo va en solicitudNexoId (0010)");
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
        var antes = _nexo.Peticiones.Count;

        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, codigo);
        _nexo.Peticiones.Count.Should().Be(antes, "sin formulario válido no se llama a Nexo");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task SinNexoConfiguradoUnFormularioSinCatalogosDeNexoQuedaEnErrorEnvio()
    {
        var ana = await _entorno.ApiSinCorreo.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = """{"tipo":"spot","nit_cliente":"900555111","linea_negocio":"Importacion","origen":"Cali","destino":"Bogotá","peso_kg":1000}""";

        using var envio = await _entorno.ApiSinCorreo.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo);
        var json = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);

        json.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("motivo_estado_costeo").GetString().Should().Contain("catálogo de Nexo");
    }

    [Fact]
    public async Task SinNexoConfiguradoUnTipoDeVehiculoNoSePuedeValidarYNoSeGuarda()
    {
        var ana = await _entorno.ApiSinCorreo.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await _entorno.ApiSinCorreo.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());

        envio.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(envio, "CATALOGOS_FLETE_NO_DISPONIBLE");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task UnTipoDeVehiculoFueraDelCatalogoMaestroEs400YNoLlegaANexo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario().Replace("\"3S3\"", "\"Tractomula\"", StringComparison.Ordinal));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Contain("tipo_vehiculo");
        await VerificarErrorNegocioAsync(respuesta, "SOLICITUD_COSTEO_INVALIDA");
        _nexo.Peticiones.Should().NotContain(p => p.Metodo == _post, "nunca se envía texto libre a Nexo");
        _nexo.Peticiones.Should().Contain(p => p.Url.AbsolutePath == "/api/catalogos/tipos-vehiculo");
    }

    [Fact]
    public async Task ElRequestANexoLlevaNotasMunicipiosHorasPorLineaYElDeadlineEnHoraDeBogota()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = Formulario()
            .Replace("\"deadline\":\"2026-10-15T17:00:00-05:00\"", "\"deadline\":\"2026-10-15\",\"notas\":\"Cita previa en puerto\",\"origen_municipio_id\":76109,\"destino_municipio_id\":11001", StringComparison.Ordinal);

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
        linea["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");

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
        var cuerpo = Formulario().Replace("\"3S3\"", "\" 3S3 \"", StringComparison.Ordinal).Replace("\"OC-7788\"", "\"  OC-7788 \"", StringComparison.Ordinal);

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
        primero.RootElement.GetProperty("lineas")[0].GetProperty("tipoVehiculo").GetString().Should().Be("3S3");
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

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
