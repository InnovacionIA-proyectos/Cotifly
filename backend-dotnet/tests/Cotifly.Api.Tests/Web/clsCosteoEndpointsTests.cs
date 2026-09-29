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
            $"SELECT CONCAT(costeoId, '|', estadoCosteo, '|', (SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones p WHERE p.cotizacionId = c.id)) FROM dbo_cotifly.Cotizaciones c WHERE folio = '{json.GetProperty("folio").GetString()}'");
        fila.Should().Be($"{_nexo.SolicitudesCreadas[^1]}|pendiente_costeo|2");
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
            $"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE usuarioId = '{ana.UsuarioId}'")).Should().Be(0);
    }

    [Fact]
    public async Task SinNexoConfiguradoLaCotizacionQuedaEnErrorEnvio()
    {
        var ana = await _entorno.ApiSinCorreo.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await _entorno.ApiSinCorreo.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var json = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);

        json.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("motivo_estado_costeo").GetString().Should().Contain("catálogo de Nexo");
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
