using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>GET /web/cotizaciones/{folio}</c> por HTTP contra SQL Server real y el Nexo falso. Detalle completo en una
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
        // El gerente ve el mismo costeo que el dueño salvo el costo bruto de las rutas, que solo es del dueño.
        deDueno.GetProperty("costeo").GetProperty("rutas")[0].GetProperty("costo_final").GetDecimal().Should().Be(1_000_000m);
        // El costo del transporte también es costo y el gerente lo ve en null; el valor de la mercancía NO es costo y se conserva.
        deDueno.GetProperty("costeo").GetProperty("rutas")[0].GetProperty("costo_transporte").ValueKind.Should().Be(JsonValueKind.Number);
        deGerente.GetProperty("costeo").GetProperty("rutas").EnumerateArray()
            .Should().OnlyContain(r => r.GetProperty("costo_final").ValueKind == JsonValueKind.Null
                && r.GetProperty("costo_transporte").ValueKind == JsonValueKind.Null);
        SinCostoFinal(deGerente.GetProperty("costeo")).Should().Be(SinCostoFinal(deDueno.GetProperty("costeo")), "el gerente ve lo mismo que el dueño");
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

    /// <summary>
    /// Después de editar (reemplazo en NEXO quitando una ruta y agregando otra) y de crear la <c>-v2</c>, <c>captura</c> y
    /// <c>costeo.rutas[]</c> siguen cruzando 1 a 1 por <c>linea_solicitud_id</c> (sin la ruta quitada); y con <c>solo_lectura</c> el gerente
    /// de la regional sigue recibiendo 404 en las acciones del dueño.
    /// </summary>
    [Fact]
    public async Task TrasEditarYCrearLaVersionNuevaCapturaYCosteoCruzanYElGerenteNoActua()
    {
        var occidente = Regional();
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        // 1. Reemplazo en NEXO: se quita la ruta 1 y se agrega una nueva al final.
        var (modelo, etag) = await ModeloAsync(ana, folio);
        var quitada = modelo["rutas"]![0]!["linea_solicitud_id"]!.GetValue<string>();
        var segunda = modelo["rutas"]![1]!.DeepClone();
        var nueva = modelo["rutas"]![1]!.DeepClone();
        nueva.AsObject().Remove("linea_solicitud_id");
        nueva["destino_municipio_id"] = 76001;
        nueva["destino"] = "Cali";
        modelo["rutas"] = new JsonArray(segunda, nueva);
        modelo["motivo"] = "Cambió el destino";
        using (var editada = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta(folio)}/solicitud", ana.Access, modelo.ToJsonString(), etag))
        {
            editada.StatusCode.Should().Be(HttpStatusCode.OK, await editada.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var tras = await DetalleAsync(ana, folio);
        var idsTras = Cruzadas(tras);
        idsTras.Should().HaveCount(2).And.NotContain(quitada);

        // 2. Costeo, entrega y resultado: editar crea la -v2, con sus propias rutas.
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 800_000m);
        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);
        await _api.FinalizarSiCorrespondeAsync(ana.Access, folio); // Como el frontend
        using (var resultado = await _api.EnviarAsync(_post, $"{Ruta(folio)}/resultado", ana.Access, """{"resultado":"ganada","motivo":"Precio"}"""))
        {
            resultado.StatusCode.Should().Be(HttpStatusCode.OK, await resultado.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        (modelo, etag) = await ModeloAsync(ana, folio);
        modelo["rutas"]![0]!["horas_cargue"] = 9;
        modelo["modo_esperado"] = "nueva_version";
        using (var version = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta(folio)}/solicitud", ana.Access, modelo.ToJsonString(), etag))
        {
            version.StatusCode.Should().Be(HttpStatusCode.Created, await version.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var v2 = await DetalleAsync(ana, $"{folio}-v2");
        Cruzadas(v2).Should().HaveCount(2).And.NotIntersectWith(idsTras, "la versión nueva tiene sus propias rutas");
        Cruzadas(await DetalleAsync(ana, folio)).Should().Equal(idsTras, "la original no cambia");

        // 3. El gerente la ve en solo consulta, pero las acciones del dueño le siguen respondiendo 404.
        using (var deGerente = await _api.EnviarAsync(_get, Ruta(folio), gloria.Access))
        {
            (await VerificarContratoAsync(deGerente, Contrato, _get, HttpStatusCode.OK)).GetProperty("solo_lectura").GetBoolean().Should().BeTrue();
        }

        foreach (var (metodo, ruta, cuerpo) in new (HttpMethod, string, string?)[]
                 {
                     (_get, $"{Ruta(folio)}/costeo", null),
                     (_get, $"{Ruta(folio)}/solicitud", null),
                     (HttpMethod.Put, $"{Ruta(folio)}/costeo/margen", """{"margen":0.3,"linea_orden":1}"""),
                     (_post, $"{Ruta(folio)}/resultado", """{"resultado":"perdida","motivo":"Valor muy alto"}"""),
                 })
        {
            using var accion = await _api.EnviarAsync(metodo, ruta, gloria.Access, cuerpo, tras.GetProperty("row_version").GetString());
            accion.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{metodo} {ruta} es del dueño");
        }
    }

    private async Task<JsonElement> DetalleAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, Ruta(folio), sesion.Access);
        return await VerificarContratoAsync(respuesta, Contrato, _get, HttpStatusCode.OK);
    }

    /// <summary>Los <c>linea_solicitud_id</c> de <c>captura</c>, comprobando que son exactamente los de <c>costeo.rutas[]</c> y en el mismo orden.</summary>
    private static List<string> Cruzadas(JsonElement detalle)
    {
        var captura = detalle.GetProperty("captura").EnumerateArray().Select(r => r.GetProperty("linea_solicitud_id").GetString()!).ToList();
        var costeo = detalle.GetProperty("costeo").GetProperty("rutas").EnumerateArray().Select(r => r.GetProperty("linea_solicitud_id").GetString()!).ToList();
        captura.Should().Equal(costeo, "captura y costeo se cruzan por linea_solicitud_id");
        return captura;
    }

    private async Task<(JsonNode Modelo, string? Etag)> ModeloAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, $"{Ruta(folio)}/solicitud", sesion.Access);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        return (JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!, respuesta.Headers.ETag?.Tag);
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

    /// <summary>
    /// El costeo serializado sin los costos de <c>rutas[]</c> (<c>costo_final</c> y <c>costo_transporte</c>):
    /// los únicos campos que cambian entre el dueño y el gerente (el valor de la mercancía no es costo y se conserva en los dos).
    /// </summary>
    private static string SinCostoFinal(JsonElement costeo)
    {
        var nodo = JsonNode.Parse(costeo.GetRawText())!;
        foreach (var ruta in nodo["rutas"]!.AsArray())
        {
            ruta!.AsObject().Remove("costo_final");
            ruta.AsObject().Remove("costo_transporte");
        }

        return nodo.ToJsonString();
    }
}
