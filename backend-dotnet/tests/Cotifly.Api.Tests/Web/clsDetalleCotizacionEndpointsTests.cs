using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-027.1 (#179, parte A): <c>GET /web/cotizaciones/{folio}</c> por HTTP contra SQL Server real y el Nexo falso. Detalle completo en una
/// llamada (cabecera, costeo por ruta, rutas capturadas, totales y <c>ETag</c>) sin consultar Nexo; acceso del dueño, del gerente de su
/// regional (solo consulta) y 404 para el gerente de otra regional y para el comercial ajeno.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsDetalleCotizacionEndpointsTests : IDisposable
{
    private const string Contrato = "/web/cotizaciones/{folio}";
    private const string Cargue = "SC-CARGUE-DET";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, Cargue, "Cargue en puerto", "logistico", "UND", 150_000m);
    private readonly clsApiWebPruebas _api;

    public clsDetalleCotizacionEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static string Regional() => $"Det-{Guid.NewGuid():N}"[..16];

    private static string Ruta(string folio) => $"/web/cotizaciones/{Uri.EscapeDataString(folio)}";

    [Fact]
    public async Task ConVariasRutasCosteadasYResultadoTraeTodoEnUnaLlamadaSinConsultarNexo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional(), "Ana Detalle");
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 800_000m);
        await _api.ProcesarPollingAsync(folio);
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET ganada = 1, motivoRechazo = N'No aplica', marcadoAt = SYSDATETIMEOFFSET() WHERE folio = '{folio}'");
        var llamadasNexo = _nexo.Peticiones.Count;

        using var respuesta = await _api.EnviarAsync(_get, Ruta(folio), ana.Access);
        var json = await VerificarContratoAsync(respuesta, Contrato, _get, HttpStatusCode.OK);

        _nexo.Peticiones.Count.Should().Be(llamadasNexo, "el detalle solo lee lo que Cotifly guardó: ni Nexo ni el polling");
        respuesta.Headers.ETag!.Tag.Trim('"').Should().Be(json.GetProperty("row_version").GetString());
        json.GetProperty("solo_lectura").GetBoolean().Should().BeFalse();
        json.GetProperty("tipo").GetString().Should().Be("spot");
        json.GetProperty("fechas").GetProperty("marcado_at").ValueKind.Should().Be(JsonValueKind.String);
        json.GetProperty("fechas").GetProperty("costeo_listo_at").ValueKind.Should().Be(JsonValueKind.String);

        var cabecera = json.GetProperty("cotizacion");
        cabecera.GetProperty("folio").GetString().Should().Be(folio);
        cabecera.GetProperty("nit_cliente").GetString().Should().Be(clsCosteoEndpointsTests.Nit);
        cabecera.GetProperty("ganada").GetBoolean().Should().BeTrue();
        cabecera.GetProperty("motivo_rechazo").GetString().Should().Be("No aplica");
        cabecera.GetProperty("deadline").GetString().Should().Be("2026-10-15T22:00:00+00:00");
        cabecera.GetProperty("linea_negocio").GetString().Should().Be("Varias");
        cabecera.GetProperty("comercial").GetProperty("usuario_id").GetString().Should().Be(ana.UsuarioId.ToString());
        cabecera.GetProperty("comercial").GetProperty("nombre_completo").GetString().Should().Be("Ana Detalle");

        var costeo = json.GetProperty("costeo");
        var rutas = costeo.GetProperty("rutas").EnumerateArray().ToList();
        rutas.Should().HaveCount(2);
        rutas.Should().OnlyContain(r => r.GetProperty("precio_final_linea").ValueKind == JsonValueKind.Number);
        rutas[1].GetProperty("servicios_conexos").EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Contain(Cargue);
        rutas.Should().OnlyContain(r => r.GetProperty("camiones").GetArrayLength() > 0 && r.GetProperty("mercancias").GetArrayLength() > 0);

        var captura = json.GetProperty("captura").EnumerateArray().ToList();
        captura.Select(r => r.GetProperty("linea_solicitud_id").GetString())
            .Should().Equal(rutas.Select(r => r.GetProperty("linea_solicitud_id").GetString()), "se cruzan por linea_solicitud_id");
        (captura[0].GetProperty("origen_municipio_id").GetInt32(), captura[0].GetProperty("destino_municipio_id").GetInt32()).Should().Be((76109, 11001));
        (captura[1].GetProperty("linea_negocio").GetString(), captura[1].GetProperty("clase_contenedor").GetString()).Should().Be(("Exportacion", "CNT20.ST"));
        captura[1].GetProperty("servicios_conexos")[0].GetProperty("id_servicio_conexo").GetString().Should().Be(Cargue);

        var totales = json.GetProperty("totales");
        totales.GetProperty("rutas").GetInt32().Should().Be(2);
        totales.GetProperty("camiones").GetInt32().Should().Be(2);
        totales.GetProperty("peso_kg").GetDecimal().Should().Be(40_500m);
        var precioFinal = costeo.GetProperty("precio_final").GetDecimal();
        totales.GetProperty("precio_final").GetDecimal().Should().Be(precioFinal);
        (totales.GetProperty("precio_transporte").GetDecimal() + totales.GetProperty("precio_conexos").GetDecimal()).Should().Be(precioFinal);
        totales.GetProperty("precio_conexos").GetDecimal().Should().BePositive();

        // `costeo` es exactamente lo que responde GET .../costeo (la misma proyección).
        using var soloCosteo = await _api.EnviarAsync(_get, $"{Ruta(folio)}/costeo", ana.Access);
        var deCosteo = await VerificarContratoAsync(soloCosteo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        costeo.GetRawText().Should().Be(deCosteo.GetRawText());
    }

    [Fact]
    public async Task SinCosteoTraeLaCabeceraYLaCapturaConLosPreciosEnNull()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional());
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var respuesta = await _api.EnviarAsync(_get, Ruta(folio), ana.Access);
        var json = await VerificarContratoAsync(respuesta, Contrato, _get, HttpStatusCode.OK);

        json.GetProperty("cotizacion").GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        json.GetProperty("cotizacion").GetProperty("ganada").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("costeo").GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("fechas").GetProperty("costeo_listo_at").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("captura").GetArrayLength().Should().Be(2);
        var totales = json.GetProperty("totales");
        totales.GetProperty("rutas").GetInt32().Should().Be(2);
        foreach (var precio in new[] { "precio_transporte", "precio_conexos", "precio_final" })
        {
            totales.GetProperty(precio).ValueKind.Should().Be(JsonValueKind.Null, precio);
        }
    }

    [Fact]
    public async Task ElDuenoYElGerenteDeSuRegionalLaVenYElGerenteDeOtraRegionalYElAjenoReciben404()
    {
        var occidente = Regional();
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Ana Occidente");
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var hugo = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());
        var sinRegional = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var deAna = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111222");

        using var dueno = await _api.EnviarAsync(_get, Ruta(deAna.Folio), ana.Access);
        using var gerente = await _api.EnviarAsync(_get, Ruta(deAna.Folio), gloria.Access);
        using var otraRegional = await _api.EnviarAsync(_get, Ruta(deAna.Folio), hugo.Access);
        using var gerenteSinRegional = await _api.EnviarAsync(_get, Ruta(deAna.Folio), sinRegional.Access);
        using var ajeno = await _api.EnviarAsync(_get, Ruta(deAna.Folio), luis.Access);
        using var inexistente = await _api.EnviarAsync(_get, Ruta("PCN-XX-01011999-999"), gloria.Access);

        var deDueno = await VerificarContratoAsync(dueno, Contrato, _get, HttpStatusCode.OK);
        var deGerente = await VerificarContratoAsync(gerente, Contrato, _get, HttpStatusCode.OK);
        deDueno.GetProperty("solo_lectura").GetBoolean().Should().BeFalse();
        deGerente.GetProperty("solo_lectura").GetBoolean().Should().BeTrue();
        deGerente.GetProperty("cotizacion").GetProperty("comercial").GetProperty("usuario_id").GetString().Should().Be(ana.UsuarioId.ToString());
        deGerente.GetProperty("cotizacion").GetProperty("comercial").GetProperty("nombre_completo").GetString().Should().Be("Ana Occidente");
        deGerente.GetProperty("costeo").GetRawText().Should().Be(deDueno.GetProperty("costeo").GetRawText(), "el gerente ve lo mismo que el dueño");
        foreach (var negada in new[] { otraRegional, gerenteSinRegional, ajeno, inexistente })
        {
            var cuerpo = await VerificarContratoAsync(negada, Contrato, _get, HttpStatusCode.NotFound);
            cuerpo.GetProperty("error").GetString().Should().Be("Cotización no encontrada o no es tuya.", "igual que si no existiera");
        }

        // Cada consulta de un gerente a una cotización ajena queda en la auditoría (permitida o no), como la descarga del PDF.
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'consultar_detalle_cotizacion' AND input LIKE '%{deAna.Folio}%'"))
            .Should().Be(3, "gloria (permitida), hugo y el gerente sin regional (negadas); ni el dueño ni el comercial ajeno");
    }

    [Fact]
    public async Task SinSesionResponde401()
    {
        using var respuesta = await _api.EnviarAsync(_get, Ruta("PCN-XX-01011999-999"), null);

        await VerificarContratoAsync(respuesta, Contrato, _get, HttpStatusCode.Unauthorized);
    }

    /// <summary>Captura con dos rutas de distinta línea; la ruta 2 lleva un conexo capturado.</summary>
    private static string Formulario() => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        deadline = "2026-10-15T17:00:00-05:00",
        notas = "Entrega en la mañana",
        horas_cargue = 4,
        horas_descargue = 6.5,
        rutas = new object[]
        {
            new
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
                mercancias = new object[] { new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
            },
            new
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
                mercancias = new object[] { new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Bobinas", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1250 } },
                servicios_conexos = new object[] { new { id_servicio_conexo = Cargue, cantidad = 1 } },
            },
        },
    });

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
