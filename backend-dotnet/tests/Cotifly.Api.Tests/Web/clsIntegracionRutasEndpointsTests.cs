using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Escenario cruzado de HU-021 (#132, #134 y #135 integradas) por HTTP con SQL Server real y el Nexo falso: una captura con dos rutas
/// de distinta línea de negocio y un conexo capturado viaja a Nexo con la <c>lineaNegocio</c> de cada ruta en cada camión; el conexo
/// que se agrega a la ruta 2 va a la línea del primer camión VIGENTE de esa ruta y vuelve a esa ruta, y el ajuste de margen de la ruta 2
/// no toca la ruta 1.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsIntegracionRutasEndpointsTests : IDisposable
{
    private const string Cargue = "SC-CARGUE-INT";
    private const string Almacenamiento = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, Cargue, "Cargue en puerto", "logistico", "UND", 150_000m)
        .ConServicioConexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", 120_000m);
    private readonly clsApiWebPruebas _api;

    public clsIntegracionRutasEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DosRutasDeDistintaLineaViajanConSuLineaElConexoVaAlPrimerCamionVigenteYElMargenSoloCambiaLaRuta2()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        // 1. Captura (#132) con Nexo caído: queda guardada en error_envio con sus dos rutas y el conexo capturado en la ruta 2.
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var creada = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        creada.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        var folio = creada.GetProperty("folio").GetString()!;

        // La ruta 2 lleva además un segundo camión (hasta el cubicador, #133), creado después del de la captura.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, tipoVehiculo, carroceria, pesoKg, costoLevanteContenedor, "
            + "estadoCosteo, createdAt, updatedAt) "
            + "SELECT NEWID(), l.id, N'2S2', N'FURGON', 6000, ISNULL(l.valorLevanteContenedor, 0), "
            + "(SELECT TOP 1 v.estadoCosteo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id), "
            + "DATEADD(SECOND, 1, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET() "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = 2");

        // 2. Envío por camión (#134): una línea por camión, cada una con la línea de negocio de Nexo de SU ruta.
        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);
        await VerificarContratoAsync(reintento, "/web/cotizaciones/{folio}/costeo/reintentar", _post, HttpStatusCode.OK);
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => (l["origen"]!.GetValue<string>(), l["peso"]!.GetValue<decimal>(), l["lineaNegocio"]!.GetValue<string>()))
            .Should().Equal(
                ("Buenaventura", 28000m, "Puerto a interior"),
                ("Medellín", 12500m, "Interior a puerto"),
                ("Medellín", 6000m, "Interior a puerto"));

        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 800_000m);
        _nexo.CostearLinea(solicitud, 2, 500_000m);
        await _api.ProcesarPollingAsync(folio);

        // El primer camión de la ruta 2 queda descartado: los conexos de la ruta van al siguiente.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE v SET estadoCosteo = 'descartada' FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = 2 AND v.pesoKg = 12500");

        var antes = await RutasAsync(ana, folio);
        antes[1].GetProperty("servicios_conexos").EnumerateArray().Select(c => c.GetProperty("nombre").GetString())
            .Should().Equal(["Cargue en puerto"], "el conexo capturado (#132) quedó en la ruta 2");
        antes[0].GetProperty("servicios_conexos").GetArrayLength().Should().Be(0);

        using var agregar = await ConVersionAsync(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", ana.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 2, servicio_id = Almacenamiento, cantidad = 1 } } }), await EtagAsync(ana, folio));
        await VerificarContratoAsync(agregar, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _post, HttpStatusCode.OK);
        var peticiones = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!);
        peticiones.Should().HaveCount(2, "el polling envió el conexo capturado (envío diferido, #136) y el POST el agregado");
        peticiones[0]["items"]!.AsArray().Single()!["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue);
        var item = peticiones[^1]["items"]!.AsArray().Single()!;
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Almacenamiento);
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, 2),
            "decisión 3 (#134): el conexo va a la línea del primer camión VIGENTE de su ruta (el de 12500 kg está descartado)");

        var conConexo = await RutasAsync(ana, folio);
        conConexo[1].GetProperty("servicios_conexos").EnumerateArray().Select(c => c.GetProperty("nombre").GetString())
            .Should().BeEquivalentTo(["Cargue en puerto", "Almacenamiento"], "el conexo vuelve a su ruta (#134/#135)");
        conConexo[0].GetProperty("servicios_conexos").GetArrayLength().Should().Be(0);

        // 3. Margen por ruta (#135): solo la ruta 2 cambia.
        var precioRuta1 = conConexo[0].GetProperty("precio_final_linea").GetDecimal();
        var precioRuta2 = conConexo[1].GetProperty("precio_final_linea").GetDecimal();
        using var margen = await ConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.30,"linea_orden":2}""", await EtagAsync(ana, folio));
        var ajustado = await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        var rutas = ajustado.GetProperty("rutas").EnumerateArray().ToList();
        rutas[0].GetProperty("margen_aplicado").GetDecimal().Should().Be(0.15m);
        rutas[0].GetProperty("origen_margen").GetString().Should().Be("politica_linea");
        rutas[0].GetProperty("precio_final_linea").GetDecimal().Should().Be(precioRuta1, "la ruta 1 no se toca");
        rutas[1].GetProperty("margen_aplicado").GetDecimal().Should().Be(0.30m);
        rutas[1].GetProperty("origen_margen").GetString().Should().Be("negociado");
        rutas[1].GetProperty("precio_final_linea").GetDecimal().Should().BeGreaterThan(precioRuta2);
        ajustado.GetProperty("precio_final").GetDecimal().Should().Be(rutas.Sum(r => r.GetProperty("precio_final_linea").GetDecimal()));
        var ruta2Id = rutas[1].GetProperty("linea_solicitud_id").GetString();
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}' AND lineaSolicitudId = '{ruta2Id}'"))
            .Should().Be(1, "el ajuste queda registrado en la ruta 2");
    }

    [Fact]
    public async Task LasMetricasDeLaCadenaCompletaCuentanPorCotizacionYSumanPorRutaSinElCamionDescartado()
    {
        // HU-021.10 (#137) sobre #132, #134 y #135: dos rutas de distinta línea, la ruta 2 con un camión descartado y un conexo
        // capturado más otro agregado, y un ajuste de margen solo en la ruta 2.
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, tipoVehiculo, carroceria, pesoKg, costoLevanteContenedor, "
            + "estadoCosteo, createdAt, updatedAt) "
            + "SELECT NEWID(), l.id, N'2S2', N'FURGON', 6000, ISNULL(l.valorLevanteContenedor, 0), "
            + "(SELECT TOP 1 v.estadoCosteo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id), "
            + "DATEADD(SECOND, 1, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET() "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = 2");
        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", ana.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);

        // El camión de 12500 kg de la ruta 2 queda descartado ANTES de leer el costeo: su flete (800.000) no debe sumar.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE v SET estadoCosteo = 'descartada' FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = 2 AND v.pesoKg = 12500");
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 800_000m);
        _nexo.CostearLinea(solicitud, 2, 500_000m);
        await _api.ProcesarPollingAsync(folio);

        using var agregar = await ConVersionAsync(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", ana.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 2, servicio_id = Almacenamiento, cantidad = 1 } } }), await EtagAsync(ana, folio));
        agregar.StatusCode.Should().Be(HttpStatusCode.OK, await agregar.Content.ReadAsStringAsync(Ct));
        using var margen = await ConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.30,"linea_orden":2}""", await EtagAsync(ana, folio));
        var costeo = await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        var rutas = costeo.GetProperty("rutas").EnumerateArray().OrderBy(r => r.GetProperty("linea_orden").GetInt32()).ToList();
        var precio1 = rutas[0].GetProperty("precio_final_linea").GetDecimal();
        var precio2 = rutas[1].GetProperty("precio_final_linea").GetDecimal();

        // Costos de las rutas en la base: ruta 1 = flete; ruta 2 = flete del camión vigente + conexos (150.000 + 120.000), sin el descartado.
        var costos = await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT STRING_AGG(CAST(CAST(l.costoFinal AS decimal(18,2)) AS nvarchar(30)), '|') WITHIN GROUP (ORDER BY l.orden) "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}'");
        costos.Should().Be("1000000.00|770000.00", "el camión descartado no suma y los conexos sí");
        precio2.Should().Be(1_100_000m, "770.000 / (1 − 0,30): el precio de la ruta incluye los conexos");
        precio1.Should().BeApproximately(1_176_470.59m, 0.5m, "1.000.000 / (1 − 0,15)");
        costeo.GetProperty("precio_final").GetDecimal().Should().Be(precio1 + precio2);

        using var respuesta = await _api.EnviarAsync(_get, "/web/metricas/comercial?desde=2026-01-01&hasta=2026-12-31", ana.Access);
        var metricas = await VerificarContratoAsync(respuesta, "/web/metricas/comercial", _get, HttpStatusCode.OK);
        var indicadores = metricas.GetProperty("indicadores");
        indicadores.GetProperty("cantidad").GetInt32().Should().Be(1, "decisión 8: dos rutas de dos líneas son UNA cotización");
        indicadores.GetProperty("pendientes_por_cotizar").GetInt32().Should().Be(0);
        indicadores.GetProperty("sin_resultado").GetInt32().Should().Be(1);
        indicadores.GetProperty("costeo_total").GetDecimal().Should().Be(1_770_000m);
        indicadores.GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio1 + precio2);
        indicadores.GetProperty("ajustes_margen").GetInt32().Should().Be(1);
        var ponderado = Math.Round((precio1 + precio2 - 1_770_000m) / (precio1 + precio2), 4, MidpointRounding.ToEven);
        indicadores.GetProperty("margen_ponderado").GetDecimal().Should().Be(ponderado, "Σ(precio − costo) / Σ precio de las rutas");

        var lineas = metricas.GetProperty("por_linea_negocio").EnumerateArray()
            .ToDictionary(l => l.GetProperty("linea_negocio").GetString()!, l => l);
        lineas.Keys.Should().BeEquivalentTo(["Importacion", "Exportacion"]);
        var importacion = lineas["Importacion"];
        importacion.GetProperty("cotizaciones").GetInt32().Should().Be(1);
        importacion.GetProperty("rutas").GetInt32().Should().Be(1);
        importacion.GetProperty("costeo_total").GetDecimal().Should().Be(1_000_000m);
        importacion.GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(precio1);
        importacion.GetProperty("margen_ponderado").GetDecimal().Should().Be(Math.Round((precio1 - 1_000_000m) / precio1, 4, MidpointRounding.ToEven));
        importacion.GetProperty("ajustes_margen").GetInt32().Should().Be(0, "el ajuste fue en la ruta 2");
        var exportacion = lineas["Exportacion"];
        exportacion.GetProperty("cotizaciones").GetInt32().Should().Be(1);
        exportacion.GetProperty("costeo_total").GetDecimal().Should().Be(770_000m);
        exportacion.GetProperty("valor_cotizado_cop").GetDecimal().Should().Be(1_100_000m);
        exportacion.GetProperty("margen_ponderado").GetDecimal().Should().Be(0.3m, "(1.100.000 − 770.000) / 1.100.000");
        exportacion.GetProperty("ajustes_margen").GetInt32().Should().Be(1);
        exportacion.GetProperty("desviacion_promedio_margen").GetDecimal().Should().Be(0.15m, "0,30 negociado − 0,15 sugerido de la ruta 2");
    }

    /// <summary>Dos rutas de distinta línea de negocio (Importacion y Exportacion); la ruta 2 captura un conexo del catálogo de Nexo.</summary>
    /// <summary>
    /// #278 (bloqueante de la validación): la línea sembrada <c>DevolucionContenedor</c> (mapeo <c>DevolucionContenedor</c>, seeder y 0009)
    /// se captura sin <c>LINEA_NEGOCIO_INVALIDO</c> y viaja a Nexo con la escritura canónica del DU, que el Nexo falso acepta.
    /// </summary>
    [Fact]
    public async Task LaLineaSembradaDevolucionContenedorViajaComoDevolucionDeContenedor()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = Formulario().Replace("\"linea_negocio\":\"Importacion\"", "\"linea_negocio\":\"DevolucionContenedor\"", StringComparison.Ordinal)
            .Replace("\"linea_negocio\":\"Exportacion\"", "\"linea_negocio\":\"DevolucionContenedor\"", StringComparison.Ordinal);

        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, formulario);

        var creada = await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        creada.GetProperty("estado_costeo").GetString().Should().NotBe("error_envio");
        _nexo.RechazosContrato.Should().BeEmpty();
        _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!["lineaNegocio"]!.GetValue<string>()).Should().OnlyContain(l => l == "Devolución de contenedor");
    }

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

    private async Task<List<JsonElement>> RutasAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return [.. json.GetProperty("rutas").EnumerateArray().OrderBy(r => r.GetProperty("linea_orden").GetInt32())];
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<HttpResponseMessage> ConVersionAsync(HttpMethod metodo, string ruta, string token, string cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
