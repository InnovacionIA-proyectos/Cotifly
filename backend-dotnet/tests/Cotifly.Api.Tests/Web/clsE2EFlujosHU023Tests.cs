using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Data.SqlClient;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Campaña de QA de HU-021 + HU-023 (rama de integración <c>hu-023-integracion</c>): un E2E por flujo, por HTTP contra la API real en
/// memoria, sobre la base instalada DESDE CERO (<c>db/scripts/01_crear_esquema.sql</c> + <c>02_alter_cubicador.sql</c> +
/// <c>pendientes/04_cubicacion_segundo_plano.sql</c>), con el Nexo, el cubicador, la TRM, el relay SMTP y el modelo falsos (nunca los
/// reales). Cada flujo verifica en la base las filas que deja.
/// </summary>
[Collection(clsColeccionApiWebInstalada.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsE2EFlujosHU023Tests : IAsyncDisposable
{
    private const string CodigoCafe = "CAFE_EN_ESTIBAS";
    private const string Cargue = "SC-CARGUE-E2E";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsBaseDatosPruebas _bd;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, Cargue, "Cargue en puerto", "logistico", "UND", 150_000m);
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsE2EFlujosHU023Tests(clsEntornoApiWebInstalada entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _bd = entorno.BaseDatos;
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso(CodigoCafe, "Café en estibas"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cadena => _bd.CadenaCotifly;

    // ============================================================================================================== Flujo 1
    /// <summary>
    /// Formulario de 2 rutas → cubicador (ruta 1: dos camiones IDÉNTICOS; ruta 2: un camión con peso del cubicador distinto del
    /// declarado) → Nexo con grupoRutaId e idRecomendacionVehiculo → polling con líneas barajadas → margen de la ruta 2 → conexo diferido →
    /// PDF → correo → re-solicitud (PDF_YA_ENVIADO y versión <c>-v2</c>) → entrega → ganada → métricas.
    /// </summary>
    [Fact]
    public async Task F1_FormularioDosRutasHastaGanadaYMetricasDejaLasFilasEsperadas()
    {
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 14_000m), clsCubicadorFalso.Vehiculo("3S3", "FURGON", 14_000m))
            : clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 12_480m));
        var api = Api(conCubicador: true, correo: true, datosCubicador: true);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var idExterno = await Escalar<long>($"SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE id = '{ana.UsuarioId}'");

        // 1. Captura
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, FormularioDosRutas());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await api.IdCotizacionAsync(folio);
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _cubicador.Recomendaciones.Should().HaveCount(2, "una llamada al cubicador por ruta");

        (await Filas($"""
            SELECT CONCAT(l.orden, '|', ISNULL(v.tipoVehiculo, '-'), '|', ISNULL(v.carroceria, '-'), '|', v.pesoKg, '|', IIF(v.idRecomendacionVehiculo IS NULL, '-', 'R'), '|', v.estadoCosteo)
            {DeLosCamiones(id)} ORDER BY l.orden, IIF(v.ordenVehiculo IS NULL, 1, 0), v.ordenVehiculo, v.createdAt, v.id
            """)).Should().Equal(
            "1|3S3|FURGON|14000.00|R|pendiente_costeo",
            "1|3S3|FURGON|14000.00|R|pendiente_costeo",
            "2|2S3|ESTACAS|12480.00|R|pendiente_costeo");
        (await Escalar<int>($"SELECT COUNT(DISTINCT v.idRecomendacionVehiculo) {DeLosCamiones(id)}")).Should().Be(3, "cada camión con su recomendación");

        // El alta: una línea por camión, con su ruta (grupoRutaId) y su camión (idRecomendacionVehiculo); la ruta de UN camión lleva el
        // peso DECLARADO (12500, no el 12480 del cubicador) y sus posiciones SIN codigoTipoMercancia (contrato de Nexo #297, #159); la de dos
        // camiones, el peso de cada uno.
        var alta = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        alta.Should().HaveCount(3);
        var rutas = await FilasPar($"SELECT CAST(id AS nvarchar(40)), CAST(orden AS nvarchar(5)) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{id}'");
        var camiones = await FilasPar($"SELECT LOWER(CAST(v.idRecomendacionVehiculo AS nvarchar(40))), CAST(l.orden AS nvarchar(5)) {DeLosCamiones(id)}");
        foreach (var linea in alta)
        {
            var grupo = linea["grupoRutaId"]?.GetValue<string>();
            var recomendacion = linea["idRecomendacionVehiculo"]?.GetValue<string>()?.ToLowerInvariant();
            var orden = camiones.Single(c => c.A == recomendacion).B;
            // HU-023 (#176, contrato con Nexo §3.3): la ruta de dos camiones lleva su grupo; la de UN camión (línea suelta), null.
            if (orden == "1")
            {
                grupo.Should().NotBeNull("Nexo:EnviarGrupoRutaId está encendida y la ruta 1 tiene dos camiones");
                rutas.Single(r => string.Equals(r.A, grupo, StringComparison.OrdinalIgnoreCase)).B.Should().Be(orden);
            }
            else
            {
                grupo.Should().BeNull("la ruta 2 tiene un solo camión: línea suelta");
            }

            camiones.Should().Contain((recomendacion!, orden), "la recomendación es de un camión de esa misma ruta");
            if (orden == "2")
            {
                linea["peso"]!.GetValue<decimal>().Should().Be(12_500m, "con un solo camión viaja el peso declarado de la ruta");
                var posiciones = linea["posiciones"]!.AsArray();
                posiciones.Should().ContainSingle();
                posiciones[0]!["codigoTipoMercancia"].Should().BeNull("con el contrato de Nexo #297 el código de mercancía no viaja (ADR-020)");
            }
            else
            {
                linea["peso"]!.GetValue<decimal>().Should().Be(14_000m);
                (linea["posiciones"]?.AsArray() ?? []).Should().BeEmpty("con varios camiones no viajan posiciones");
            }
        }

        // Identidad: TODA petición a Nexo con X-Portal-clsUsuario-Id = idUsuarioExterno del comercial.
        _nexo.Peticiones.Should().OnlyContain(p => p.IdPortalUsuario == idExterno.ToString(CultureInfo.InvariantCulture));

        // 2. Polling con las líneas barajadas: los dos camiones idénticos reciben CADA UNO el flete de su línea (grupo + recomendación).
        var indiceA = alta.FindIndex(l => l["peso"]!.GetValue<decimal>() == 14_000m);
        var indiceB = alta.FindLastIndex(l => l["peso"]!.GetValue<decimal>() == 14_000m);
        var indiceRuta2 = alta.FindIndex(l => l["peso"]!.GetValue<decimal>() == 12_500m);
        var recomendacionA = alta[indiceA]["idRecomendacionVehiculo"]!.GetValue<string>();
        var recomendacionB = alta[indiceB]["idRecomendacionVehiculo"]!.GetValue<string>();
        _nexo.SemillaOrdenLineas = 20261013;
        _nexo.CostearLinea(solicitud, indiceA, 1_000_000m);
        _nexo.CostearLinea(solicitud, indiceB, 1_100_000m);
        _nexo.CostearLinea(solicitud, indiceRuta2, 600_000m);
        await api.ProcesarPollingAsync(folio);

        (await Escalar<decimal>($"SELECT v.costoFlete {DeLosCamiones(id)} AND v.idRecomendacionVehiculo = '{recomendacionA}'")).Should().Be(1_000_000m);
        (await Escalar<decimal>($"SELECT v.costoFlete {DeLosCamiones(id)} AND v.idRecomendacionVehiculo = '{recomendacionB}'")).Should().Be(1_100_000m);
        (await Escalar<int>($"SELECT COUNT(DISTINCT v.lineaSolicitudNexoId) {DeLosCamiones(id)}")).Should().Be(3);
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', l.costoFletesTotal, '|', l.costoServiciosConexos, '|', l.costoFinal, '|', l.margenAplicado, '|', l.precioFinalLinea, '|', l.estadoCosteo)
            FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = '{id}' ORDER BY l.orden
            """)).Should().Equal(
            "1|2100000.00|150000.00|2250000.00|0.1500|2647059.00|autocosteo",
            "2|600000.00|0.00|600000.00|0.1500|705882.00|autocosteo");

        // 3. Conexo diferido: viaja a la línea de Nexo del PRIMER camión de la ruta 1.
        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        var primerCamion = await Escalar<string>($"SELECT TOP 1 LOWER(CAST(v.idRecomendacionVehiculo AS nvarchar(40))) {DeLosCamiones(id)} AND l.orden = 1 ORDER BY IIF(v.ordenVehiculo IS NULL, 1, 0), v.ordenVehiculo, v.createdAt, v.id");
        var indicePrimero = alta.FindIndex(l => string.Equals(l["idRecomendacionVehiculo"]!.GetValue<string>(), primerCamion, StringComparison.OrdinalIgnoreCase));
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, indicePrimero));

        // 4. Margen de la ruta 2 (If-Match).
        using (var margen = await api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.30,"linea_orden":2,"justificacion":"Cliente estratégico"}""", await EtagAsync(api, ana, folio)))
        {
            await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        }

        (await Fila($"SELECT CONCAT(margenAplicado, '|', origenMargen, '|', precioFinalLinea) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{id}' AND orden = 2"))
            .Should().Be("0.3000|negociado|857143.00");
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE cotizacionId = '{id}'")).Should().Be(1);
        var precio = await Escalar<decimal>($"SELECT precioFinal FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'");
        precio.Should().Be(2_647_059m + 857_143m);

        // 5. PDF y correo.
        await api.FinalizarSiCorrespondeAsync(ana.Access, folio); // HU-036.3: como el frontend
        using (var pdf = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", ana.Access))
        {
            await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        }

        using (var correo = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/enviar", ana.Access, """{"para":["compras@longyin.com.co"]}"""))
        {
            correo.StatusCode.Should().Be(HttpStatusCode.OK, await correo.Content.ReadAsStringAsync(Ct));
        }

        (await Fila($"SELECT CONCAT(estadoEnvio, '|', IIF(pdfPath IS NULL, '-', 'P'), '|', pdfDesactualizado) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'"))
            .Should().Be("enviada|P|0");
        // HU-033 (#199): MailSender lo encola (202) y el worker de correos confirma la entrega.
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.Correos WHERE cotizacionId = '{id}' AND estado = 'encolado' AND idMailSender IS NOT NULL")).Should().Be(1);
        api.MailSender.Entregar(api.MailSender.Recibidos[^1].Id);
        (await api.ProcesarCorreosAsync(folio)).Should().Be(1);
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.Correos WHERE cotizacionId = '{id}' AND estado = 'enviado' AND siguienteConsultaAt IS NULL")).Should().Be(1);

        // 6. Ya enviada: re-solicitar en el mismo folio es PDF_YA_ENVIADO; con nueva_version crea la -v2 sin tocar la original.
        using (var mismo = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"El cliente pidió revisar"}""", await EtagAsync(api, ana, folio)))
        {
            mismo.StatusCode.Should().Be(HttpStatusCode.Conflict);
            await VerificarErrorNegocioAsync(mismo, "PDF_YA_ENVIADO");
        }

        var altasAntes = _nexo.SolicitudesCreadas.Count;
        string folioV2;
        using (var v2 = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"Nueva versión para el cliente","nueva_version":true}""", await EtagAsync(api, ana, folio)))
        {
            var json = await VerificarContratoAsync(v2, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, HttpStatusCode.Created);
            folioV2 = json.GetProperty("folio").GetString()!;
        }

        folioV2.Should().Be($"{folio}-v2");
        _nexo.SolicitudesCreadas.Should().HaveCount(altasAntes + 1, "la versión nueva es una captura nueva en Nexo");
        var idV2 = await api.IdCotizacionAsync(folioV2);
        (await Fila($"""
            SELECT CONCAT(c.version, '|', c.estadoCosteo, '|', c.estadoEnvio, '|', IIF(c.precioFinal IS NULL, '-', 'P'), '|',
                (SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = c.id), '|',
                (SELECT COUNT(*) {DeLosCamiones(idV2)}), '|',
                (SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId WHERE l.cotizacionSolicitudId = c.id))
            FROM dbo_cotifly.CotizacionesSolicitud c WHERE c.id = '{idV2}'
            """)).Should().Be("2|pendiente_costeo|sin_enviar|-|2|3|1", "la -v2 copia rutas, camiones y conexos y vuelve a costear");
        (await Fila($"SELECT CONCAT(estadoEnvio, '|', precioFinal, '|', estadoCosteo) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'"))
            .Should().Be($"enviada|{precio.ToString("0.00", CultureInfo.InvariantCulture)}|autocosteo", "la original no cambia");

        // 7. La Mesa costea y entrega la -v2 (la vigente) y el comercial la marca ganada; las métricas cuentan UNA cadena ganada con el
        // precio de la vigente (ADR-016 §2).
        var solicitudV2 = _nexo.SolicitudesCreadas[^1];
        _nexo.LineasDe(solicitudV2).Should().HaveCount(3, "la -v2 viaja con un camión por línea, igual que la original");
        _nexo.Costear(solicitudV2, 1_000_000m);
        _nexo.Entregar(solicitudV2);
        await api.ProcesarPollingAsync(folioV2);
        var precioV2 = await Escalar<decimal>($"SELECT precioFinal FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{idV2}'");
        precioV2.Should().BePositive();
        await api.FinalizarSiCorrespondeAsync(ana.Access, folioV2); // HU-036.3: como el frontend
        using (var ganada = await api.EnviarAsync(_post, $"/web/cotizaciones/{folioV2}/resultado", ana.Access, """{"resultado":"ganada"}""",
            await EtagAsync(api, ana, folioV2)))
        {
            (await VerificarContratoAsync(ganada, "/web/cotizaciones/{folio}/resultado", _post, HttpStatusCode.OK)).GetProperty("ganada").GetBoolean().Should().BeTrue();
        }

        _nexo.Resultado(solicitudV2).Should().Be("Ganada");
        (await Fila($"SELECT CONCAT(estadoCosteo, '|', CAST(ganada AS nvarchar(1)), '|', IIF(costeoSiguienteConsultaAt IS NULL, 'sin-agenda', 'agenda')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{idV2}'"))
            .Should().Be("entregada|1|sin-agenda");
        using var metricas = await api.EnviarAsync(_get, "/web/metricas/comercial", ana.Access);
        var indicadores = (await VerificarContratoAsync(metricas, "/web/metricas/comercial", _get, HttpStatusCode.OK)).GetProperty("indicadores");
        indicadores.GetProperty("cantidad").GetInt32().Should().Be(1, "la original y la -v2 son una sola cadena");
        indicadores.GetProperty("ganadas").GetInt32().Should().Be(1);
        indicadores.GetProperty("valor_ganado_cop").GetDecimal().Should().Be(precioV2);

        // Traza: la cadena determinista quedó auditada.
        var herramientas = await Filas($"SELECT herramienta FROM dbo_cotifly.EventosHerramienta WHERE input LIKE '%{folio}%' OR outputResumen LIKE '%{folio}%'");
        herramientas.Should().Contain(["enviar_costeo_nexo", "costeo_nexo_estado", "conexos_envio_nexo", "ajustar_margen_costeo", "generar_pdf_costeo", "enviar_correo"]);
    }

    // ============================================================================================================== Flujo 2
    /// <summary>
    /// Licitación de 10 rutas: análisis del Excel → <c>POST /web/licitaciones</c> (se guarda sin cubicar) → cubicación en segundo plano →
    /// una sola alta a Nexo con los datos del cubicador → costeo y polling → PDF del pliego → Excel exportado.
    /// </summary>
    [Fact]
    public async Task F2_LicitacionDeDiezRutasSeCubicaEnSegundoPlanoSaleANexoYExportaPdfYExcel()
    {
        _cubicador.Recomendar = p => clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", clsCubicadorFalso.PesoTotal(p)));

        // Cuatro llamadas en paralelo y el cubicador sin demora: las dos últimas rutas pueden terminar juntas (B2, #174). La que no obtiene
        // el candado del envío espera y vuelve a revisar, así que la licitación sale de una vez y una sola vez.
        var api = Api(conCubicador: true, datosCubicador: true, mas: new Dictionary<string, string?> { ["Cubicador:MaximoParalelo"] = "4", ["Cubicador:MaximoConcurrente"] = "4" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using (var analisis = await api.EnviarContenidoAsync(_post, "/web/licitaciones/analizar-excel", ana.Access, clsApiWebPruebas.Archivo(clsPliegoMuestra.Excel(10))))
        {
            await VerificarContratoAsync(analisis, "/web/licitaciones/analizar-excel", _post, HttpStatusCode.OK);
        }

        using var envio = await api.EnviarAsync(_post, "/web/licitaciones", ana.Access, Licitacion(10));
        var folio = (await VerificarContratoAsync(envio, "/web/licitaciones", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await api.IdCotizacionAsync(folio);
        _nexo.UltimaAlta.Should().BeNull("se guarda sin esperar al cubicador");
        (await Fila($"""
            SELECT CONCAT(c.tipoSolicitud, '|', c.estadoCosteo, '|', COUNT(l.id), '|', SUM(IIF(l.estadoCubicacion = 'pendiente', 1, 0)))
            FROM dbo_cotifly.CotizacionesSolicitud c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id
            WHERE c.id = '{id}' GROUP BY c.tipoSolicitud, c.estadoCosteo
            """)).Should().Be("licitacion|pendiente_envio|10|10");

        (await api.ProcesarCubicacionAsync(folio)).Should().Be(10);

        (await Fila($"""
            SELECT CONCAT(SUM(IIF(l.estadoCubicacion = 'cubicada', 1, 0)), '|', SUM(l.cubicacionIntentos), '|', SUM(IIF(l.errorCubicacion IS NULL, 0, 1)), '|',
                SUM(IIF(l.cubicacionDisponibleAt IS NULL, 0, 1)), '|', SUM(IIF(l.avisosCubicacion IS NULL, 0, 1)))
            FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = '{id}'
            """)).Should().Be("10|0|0|0|10", "las 10 rutas cubicadas, sin errores ni reclamo pendiente y con sus avisos guardados (04)");
        (await Escalar<int>($"SELECT COUNT(*) {DeLosCamiones(id)} AND v.idRecomendacionVehiculo IS NOT NULL AND v.tipoVehiculo = '3S3'")).Should().Be(10,
            "el camión provisional se reemplazó por el del cubicador");

        _nexo.SolicitudesCreadas.Should().ContainSingle("todo o nada hacia Nexo");
        var solicitud = _nexo.SolicitudesCreadas[^1];
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray();
        lineas.Should().HaveCount(10);
        // HU-023 (#176): cada ruta tiene un solo camión, así que todas son líneas sueltas (grupoRutaId = null).
        lineas.Should().OnlyContain(l => l!["idRecomendacionVehiculo"] != null && l["grupoRutaId"] == null && l["tipoVehiculo"]!.GetValue<string>() == "3S3");
        (await Fila($"SELECT estadoCosteo FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be("pendiente_costeo");

        _nexo.Costear(solicitud, 1_700_000m);
        await api.ProcesarPollingAsync(folio);
        (await Fila($"""
            SELECT CONCAT(c.estadoCosteo, '|', c.precioFinal, '|', SUM(IIF(l.precioFinalLinea = 2000000, 1, 0)))
            FROM dbo_cotifly.CotizacionesSolicitud c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id
            WHERE c.id = '{id}' GROUP BY c.estadoCosteo, c.precioFinal
            """)).Should().Be("autocosteo|20000000.00|10", "10 rutas de 1.700.000 con el 15 % de Nacional");

        await api.FinalizarSiCorrespondeAsync(ana.Access, folio); // HU-036.3: como el frontend
        using (var pdf = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", ana.Access))
        {
            await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        }

        (await Fila($"SELECT CONCAT(IIF(pdfPath IS NULL, '-', 'P'), '|', pdfDesactualizado) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be("P|0");

        using var excel = await api.EnviarAsync(_get, $"/web/licitaciones/{folio}/excel", ana.Access);
        excel.StatusCode.Should().Be(HttpStatusCode.OK);
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync(Ct)));
        libro.Worksheet("Camiones Cotifly").RowsUsed().Count().Should().Be(11, "encabezado + un camión por ruta");
    }

    /// <summary>
    /// Regresión de B2 (#174): 20 licitaciones de 10 rutas seguidas, con <c>Cubicador:MaximoParalelo = 4</c> y el cubicador sin demora. Antes
    /// del arreglo, cuando las dos últimas rutas terminaban juntas, ninguna enviaba (3 de 5 corridas). Cada licitación debe salir a Nexo al
    /// terminar su última ruta, sin esperar a la recuperación, y una sola vez.
    /// </summary>
    [Fact]
    public async Task F2_LasDosUltimasRutasQueTerminanJuntasEnvianLaLicitacionUnaSolaVezEnVeinteRepeticiones()
    {
        var api = Api(conCubicador: true, datosCubicador: true, mas: new Dictionary<string, string?> { ["Cubicador:MaximoParalelo"] = "4", ["Cubicador:MaximoConcurrente"] = "4" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        // COTIFLY_REPETICIONES_B2 sube las repeticiones para las corridas de carga (50 en la revisión de #174).
        var repeticiones = int.TryParse(Environment.GetEnvironmentVariable("COTIFLY_REPETICIONES_B2"), out var pedidas) && pedidas > 0 ? pedidas : 20;
        for (var repeticion = 1; repeticion <= repeticiones; repeticion++)
        {
            // Las dos últimas llamadas al cubicador responden a la vez (se esperan una a la otra, con tope): sus rutas terminan juntas.
            var llamadas = 0;
            using var ultimas = new CountdownEvent(2);
            _cubicador.Recomendar = p =>
            {
                if (Interlocked.Increment(ref llamadas) > 8)
                {
                    ultimas.Signal();
                    ultimas.Wait(TimeSpan.FromSeconds(5));
                }

                return clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", clsCubicadorFalso.PesoTotal(p)));
            };
            var antes = _nexo.SolicitudesCreadas.Count;
            using var envio = await api.EnviarAsync(_post, "/web/licitaciones", ana.Access, Licitacion(10));
            envio.StatusCode.Should().Be(HttpStatusCode.Created, await envio.Content.ReadAsStringAsync(Ct));
            using var cuerpo = JsonDocument.Parse(await envio.Content.ReadAsStringAsync(Ct));
            var folio = cuerpo.RootElement.GetProperty("folio").GetString()!;
            var id = await api.IdCotizacionAsync(folio);

            // Tope de 90 s (no 30): bajo la carga de la suite completa, abrir una conexión puede tardar el tiempo de conexión de SqlClient
            // (15 s) antes de fallar; la ruta sigue pendiente y la vuelta siguiente la cubica. La verificación no cambia: las 10 rutas.
            (await api.ProcesarCubicacionAsync(folio, TimeSpan.FromSeconds(90))).Should().Be(10, $"repetición {repeticion}");

            _nexo.SolicitudesCreadas.Count.Should().Be(antes + 1, $"repetición {repeticion}: la licitación sale a Nexo al cubicar la última ruta, una sola vez");
            (await Fila($"SELECT CONCAT(estadoCosteo, '|', IIF(solicitudNexoId IS NULL, '-', 'S')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'"))
                .Should().Be("pendiente_costeo|S", $"repetición {repeticion}");
        }
    }

    // ============================================================================================================== Flujo 3
    /// <summary>"Calcular cubicaje" (<c>POST /web/cubicacion</c>) sobre la base instalada: responde los vehículos y no deja ninguna fila.</summary>
    [Fact]
    public async Task F3_CalcularCubicajeRespondeLosVehiculosSinDejarFilasNiLlamarANexo()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 4_000m));
        var api = Api(conCubicador: true);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var antes = await ConteosAsync();

        using var respuesta = await api.EnviarAsync(_post, "/web/cubicacion", ana.Access, clsApiWebPruebas.Json(new
        {
            items = new[] { new { codigo_mercancia = CodigoCafe, cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 2000 } },
        }));

        var json = await VerificarContratoAsync(respuesta, "/web/cubicacion", _post, HttpStatusCode.OK);
        json.GetProperty("resultado").GetString().Should().Be("ASIGNADO");
        json.GetProperty("vehiculos").GetArrayLength().Should().Be(2);
        _cubicador.Recomendaciones.Should().ContainSingle().Which["idLineaSolicitud"]!.GetValue<string>().Should().StartWith("PRUEBA-");
        (await ConteosAsync()).Should().Equal(antes, "Calcular cubicaje no guarda nada");
        _nexo.SolicitudesCreadas.Should().BeEmpty();
    }

    // ============================================================================================================== Flujo 4
    /// <summary>Re-solicitud en el MISMO folio de un costeo vencido: captura nueva en Nexo, mismo folio, polling reanudado y auditoría.</summary>
    [Fact]
    public async Task F4_ReSolicitudEnElMismoFolioDejaElCosteoNuevoYLaAuditoria()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await api.IdCotizacionAsync(folio);
        var anterior = _nexo.SolicitudesCreadas[^1];
        await api.ProcesarPollingAsync(folio, TimeSpan.FromHours(73));
        (await Fila($"SELECT estadoCosteo FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be("costeo_vencido");
        var versionAntes = await Escalar<int>($"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'");

        using var respuesta = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            """{"motivo":"El costeo venció"}""", await EtagAsync(api, ana, folio));

        (await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, HttpStatusCode.OK)).GetProperty("folio").GetString().Should().Be(folio);
        var nueva = _nexo.SolicitudesCreadas[^1];
        nueva.Should().NotBe(anterior);
        (await Fila($"""
            SELECT CONCAT(solicitudNexoId, '|', estadoCosteo, '|', version, '|', IIF(costeoSiguienteConsultaAt IS NULL, '-', 'A'), '|', IIF(versionNegocio > {versionAntes}, 'sube', 'igual'))
            FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'
            """)).Should().Be($"{nueva}|pendiente_costeo|1|A|sube");
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio LIKE '{folio}%'")).Should().Be(1, "no se crea versión");
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE input LIKE '%{folio}%' AND input LIKE '%El costeo venció%'"))
            .Should().BeGreaterThan(0, "la re-solicitud queda auditada con su motivo");

        _nexo.Costear(nueva, 2_550_000m);
        await api.ProcesarPollingAsync(folio);
        (await Fila($"SELECT CONCAT(estadoCosteo, '|', precioFinal) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be("autocosteo|3000000.00");
    }

    // ============================================================================================================== Flujo 5
    /// <summary>
    /// Login (por nombre de usuario) → la identidad viaja a Nexo en <c>X-Portal-clsUsuario-Id</c> → refresh (rota el token) → logout
    /// (revoca el refresh y el access): las filas de <c>UsuariosWeb</c>, <c>TokensRefresco</c> y <c>TokensRevocados</c>.
    /// </summary>
    [Fact]
    public async Task F5_LoginRefreshYLogoutConLaIdentidadQueViajaANexo()
    {
        var api = Api();
        var nombre = $"e2e{Guid.NewGuid():N}"[..20];
        var idPortal = clsBaseDatosPruebas.NuevoIdExterno();
        await _bd.CrearPersonaExternaConIdAsync(idPortal, nombre.ToUpperInvariant(), "Persona E2E", $"{nombre}@example.com", clsBaseDatosPruebas.RolExternoComercial);

        using var login = await api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre }, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(Ct));
        using var jLogin = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct));
        var access = jLogin.RootElement.GetProperty("access").GetString()!;
        var refresh = jLogin.RootElement.GetProperty("refresh").GetString()!;
        var usuarioId = jLogin.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        (await Escalar<long>($"SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE id = '{usuarioId}'")).Should().Be(idPortal);
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.TokensRefresco WHERE usuarioId = '{usuarioId}' AND revocadoAt IS NULL")).Should().Be(1);

        using (var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", access, clsCosteoEndpointsTests.Formulario()))
        {
            envio.StatusCode.Should().Be(HttpStatusCode.Created, await envio.Content.ReadAsStringAsync(Ct));
        }

        _nexo.Peticiones.Should().NotBeEmpty().And.OnlyContain(p => p.IdPortalUsuario == idPortal.ToString(CultureInfo.InvariantCulture));

        using var rotacion = await api.Cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh }, Ct);
        rotacion.StatusCode.Should().Be(HttpStatusCode.OK);
        using var jRot = JsonDocument.Parse(await rotacion.Content.ReadAsStringAsync(Ct));
        var access2 = jRot.RootElement.GetProperty("access").GetString()!;
        var refresh2 = jRot.RootElement.GetProperty("refresh").GetString()!;
        refresh2.Should().NotBe(refresh);
        (await Fila($"""
            SELECT CONCAT(COUNT(*), '|', SUM(IIF(revocadoAt IS NULL, 1, 0)), '|', SUM(IIF(reemplazadoPorId IS NULL, 0, 1)), '|', COUNT(DISTINCT familiaId))
            FROM dbo_cotifly.TokensRefresco WHERE usuarioId = '{usuarioId}'
            """)).Should().Be("2|1|1|1", "el refresh rota en la misma familia: el viejo queda reemplazado");

        using (var logout = new HttpRequestMessage(_post, "/api/auth/logout") { Content = JsonContent.Create(new { refresh = refresh2 }) })
        {
            logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access2);
            using var respuesta = await api.Cliente.SendAsync(logout, Ct);
            respuesta.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.TokensRefresco WHERE usuarioId = '{usuarioId}' AND revocadoAt IS NULL")).Should().Be(0);
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.TokensRevocados WHERE usuarioId = '{usuarioId}'")).Should().Be(1, "el jti del access queda revocado");
        using (var tarde = await api.EnviarAsync(_get, "/web/mis-cotizaciones", access2))
        {
            tarde.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var otraVez = await api.Cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = refresh2 }, Ct);
        otraVez.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ============================================================================================================== Flujo 6
    /// <summary>El asistente PROPONE ajustar el margen (sin montos hacia el modelo) y el comercial confirma con el endpoint determinista.</summary>
    [Fact]
    public async Task F6_ElAsistenteProponeYElComercialConfirmaSinMontosAlModelo()
    {
        var modelo = new clsChatClientGuionizado()
            .Llamar("consultar_cotizacion", new { })
            .Llamar("proponer_ajuste_margen", new { margen = 0.18m })
            .Proponer("Te propongo subir el margen de la ruta al 18 %.");
        var api = Api(modelo: modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await api.IdCotizacionAsync(folio);
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await api.ProcesarPollingAsync(folio);
        var versionAntes = await Escalar<int>($"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'");

        using var chat = await api.EnviarAsync(_post, "/web/chat", ana.Access, clsApiWebPruebas.Json(new { mensaje = "Súbele el margen al 18 %", sesion_id = Guid.NewGuid().ToString("N"), folio }));
        var respuesta = await VerificarContratoAsync(chat, "/web/chat", _post, HttpStatusCode.OK);
        var accion = respuesta.GetProperty("accion_propuesta");
        accion.ValueKind.Should().Be(JsonValueKind.Object);

        // Proponer no cambia nada.
        (await Escalar<int>($"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be(versionAntes);
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE cotizacionId = '{id}'")).Should().Be(0);
        var alModelo = string.Join("\n", modelo.Llamadas.SelectMany(l => l.Mensajes).Select(m => m.Text).Concat(modelo.Llamadas.SelectMany(l => l.ResultadosHerramientas)));
        alModelo.Should().NotContain("2550000").And.NotContain("2.550.000").And.NotContain("3000000").And.NotContain("3.000.000", "la IA nunca recibe montos");

        // Confirmar = el endpoint determinista con el cuerpo exacto y el row_version de la propuesta.
        using (var confirmar = new HttpRequestMessage(new HttpMethod(accion.GetProperty("metodo").GetString()!), accion.GetProperty("endpoint").GetString()!))
        {
            confirmar.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ana.Access);
            confirmar.Headers.TryAddWithoutValidation("If-Match", $"\"{accion.GetProperty("row_version").GetString()}\"");
            confirmar.Content = new StringContent(accion.GetProperty("cuerpo").GetRawText(), System.Text.Encoding.UTF8, "application/json");
            using var ok = await api.Cliente.SendAsync(confirmar, Ct);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
        }

        (await Fila($"SELECT CONCAT(l.margenAplicado, '|', l.origenMargen, '|', c.precioFinal) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.id = '{id}'"))
            .Should().Be("0.1800|negociado|3109756.00");
        (await Fila($"SELECT CONCAT(margenPolitica, '|', margenAplicado, '|', usuarioId) FROM dbo_cotifly.MargenAjustes WHERE cotizacionId = '{id}'"))
            .Should().Be($"0.1500|0.1800|{ana.UsuarioId.ToString().ToUpperInvariant()}");
    }

    // ============================================================================================================== Flujo 7
    /// <summary>Cotización en USD: la TRM se fija al crear; el costo queda en COP y el precio en USD con la TRM fijada.</summary>
    [Fact]
    public async Task F7_EnUsdLaTrmSeFijaAlCrearYElPrecioSeConvierte()
    {
        using var dolarApi = new clsDolarApiFalso { Valor = 4000m, FechaActualizacion = clsDolarApiFalso.HoyALasOchoEnBogota() };
        var api = Api(dolarApi: dolarApi);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = JsonNode.Parse(clsCosteoEndpointsTests.Formulario())!;
        cuerpo["moneda"] = "USD";

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, cuerpo.ToJsonString());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await api.IdCotizacionAsync(folio);
        (await Fila($"SELECT CONCAT(moneda, '|', trmAplicada) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be("USD|4000.0000");

        dolarApi.Valor = 4200m; // otro día: la TRM fijada no cambia
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await api.ProcesarPollingAsync(folio);

        (await Fila($"""
            SELECT CONCAT(c.moneda, '|', c.trmAplicada, '|', c.precioFinal, '|', l.costoFinal, '|', l.precioFinalLinea)
            FROM dbo_cotifly.CotizacionesSolicitud c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id WHERE c.id = '{id}'
            """)).Should().Be("USD|4000.0000|750.00|2550000.00|750.00", "el costo en COP, el precio en USD con la TRM fijada");
        using var vista = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var json = await VerificarContratoAsync(vista, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        json.GetProperty("moneda").GetString().Should().Be("USD");
    }

    // ============================================================================================================== Flujo 8
    /// <summary>
    /// Seguimiento y resultado: una cotización de Nexo se cierra con <c>/resultado</c> (perdida con motivo; el comentario solo va a Nexo)
    /// y <c>/seguimiento</c> la redirige (USAR_RESULTADO_NEXO); una descartada se cierra con <c>/seguimiento</c>.
    /// </summary>
    [Fact]
    public async Task F8_SeguimientoYResultadoDejanElCierreEnLaBaseYEnNexo()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.Costear(solicitud, 2_550_000m);
        _nexo.Entregar(solicitud);
        await api.ProcesarPollingAsync(folio);

        using (var seguimiento = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/seguimiento", ana.Access, """{"ganada":true}"""))
        {
            seguimiento.StatusCode.Should().Be(HttpStatusCode.Conflict);
            await VerificarErrorNegocioAsync(seguimiento, "USAR_RESULTADO_NEXO");
        }

        await api.FinalizarSiCorrespondeAsync(ana.Access, folio); // HU-036.3: como el frontend
        using (var perdida = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", ana.Access,
            """{"resultado":"perdida","motivo":"Precio alto","comentario":"Pidió 5 % menos"}""", await EtagAsync(api, ana, folio)))
        {
            await VerificarContratoAsync(perdida, "/web/cotizaciones/{folio}/resultado", _post, HttpStatusCode.OK);
        }

        _nexo.Resultado(solicitud).Should().Be("Perdida");
        (await Fila($"SELECT CONCAT(CAST(ganada AS nvarchar(1)), '|', motivoRechazo, '|', estadoCosteo) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be("0|Precio alto|entregada", "el comentario no se guarda en Cotifly");

        // Una descartada por la Mesa se cierra con el seguimiento interino.
        using var otra = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio2 = (await VerificarContratoAsync(otra, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.Descartar(_nexo.SolicitudesCreadas[^1]);
        await api.ProcesarPollingAsync(folio2);
        using (var descartada = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio2}/seguimiento", ana.Access, """{"ganada":false,"motivo_rechazo":"Descartada"}"""))
        {
            (await VerificarContratoAsync(descartada, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.OK)).GetProperty("ganada").GetBoolean().Should().BeFalse();
        }

        (await Fila($"SELECT CONCAT(CAST(ganada AS nvarchar(1)), '|', motivoRechazo) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio2}'")).Should().Be("0|Descartada");
        using var mis = await api.EnviarAsync(_get, "/web/mis-cotizaciones", ana.Access);
        (await VerificarContratoAsync(mis, "/web/mis-cotizaciones", _get, HttpStatusCode.OK)).GetRawText().Should().Contain(folio).And.Contain(folio2);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var api in _apis)
        {
            api.Dispose();
        }

        _nexo.Dispose();
        _cubicador.Dispose();
    }

    private clsApiWebPruebas Api(
        bool conCubicador = false, bool correo = false, bool datosCubicador = false, clsChatClientGuionizado? modelo = null, HttpMessageHandler? dolarApi = null,
        IDictionary<string, string?>? mas = null)
    {
        var extra = new Dictionary<string, string?>(mas ?? new Dictionary<string, string?>());
        if (datosCubicador)
        {
            extra["Nexo:EnviarGrupoRutaId"] = "true";
            extra["Nexo:EnviarDatosCubicador"] = "true";

            // Integración final HU-023: con #159 los códigos de vehículo y carrocería del cubicador viajan solo con esta bandera (Nexo #297).
            // El E2E simula Nexo con el contrato completo; la bandera apagada la cubren clsCosteoNexoTests y el fuzz del emparejador.
            extra["Nexo:EnviarCodigosCubicador"] = "true";
        }

        if (correo)
        {
            extra["Correo:Habilitado"] = "true";
        }

        var api = clsApiWebPruebas.ConNexo(_bd, _nexo, extra, dolarApi: dolarApi, modelo: modelo, cubicador: conCubicador ? _cubicador : null);
        _apis.Add(api);
        return api;
    }

    private static async Task<string> EtagAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        costeo.StatusCode.Should().Be(HttpStatusCode.OK);
        return costeo.Headers.ETag!.Tag;
    }

    private async Task<IReadOnlyList<int>> ConteosAsync()
    {
        string[] tablas = ["CotizacionesSolicitud", "CotizacionLineasSolicitud", "CotizacionVehiculosLineaSolicitud", "CotizacionMercancias", "EventosHerramienta"];
        var conteos = new List<int>();
        foreach (var tabla in tablas)
        {
            conteos.Add(await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.{tabla}"));
        }

        return conteos;
    }

    private async Task<T> Escalar<T>(string sql) => (await clsBaseDatosPruebas.EscalarAsync<T>(Cadena, sql))!;

    private async Task<string> Fila(string sql) => (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena, sql))!;

    private async Task<IReadOnlyList<string>> Filas(string sql)
    {
        var filas = new List<string>();
        await using var conexion = new SqlConnection(Cadena);
        await conexion.OpenAsync(Ct);
        await using var comando = new SqlCommand(sql, conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        while (await lector.ReadAsync(Ct))
        {
            filas.Add(lector.GetString(0));
        }

        return filas;
    }

    private async Task<IReadOnlyList<(string A, string B)>> FilasPar(string sql)
    {
        var filas = new List<(string, string)>();
        await using var conexion = new SqlConnection(Cadena);
        await conexion.OpenAsync(Ct);
        await using var comando = new SqlCommand(sql, conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        while (await lector.ReadAsync(Ct))
        {
            filas.Add((lector.GetString(0), lector.GetString(1)));
        }

        return filas;
    }

    private static string DeLosCamiones(Guid id) =>
        "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
        + $"WHERE l.cotizacionSolicitudId = '{id}'";

    /// <summary>Dos rutas: la 1 (Importacion, 28000 kg, con un conexo) y la 2 (Exportacion, 12500 kg declarados).</summary>
    private static string FormularioDosRutas() => clsApiWebPruebas.Json(new
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
                mercancias = new object[] { new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
                servicios_conexos = new object[] { new { id_servicio_conexo = Cargue, cantidad = 1 } },
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
                mercancias = new object[] { new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Café verde", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1250 } },
            },
        },
    });

    /// <summary>Licitación de <paramref name="rutas"/> filas; la fila <c>i</c> lleva 10 estibas de café de <c>1000 + i</c> kg.</summary>
    private static string Licitacion(int rutas) => JsonSerializer.Serialize(new
    {
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        linea_negocio = "Nacional",
        lineas = Enumerable.Range(0, rutas).Select(i => new
        {
            fila = i,
            origen = "BUENAVENTURA",
            destino = "BOGOTA, D.C.",
            tipologia = "CargaSeca",
            peso_kg = 10 * (1000 + i),
            mercancias = new[] { new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Café", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1000 + i } },
        }).ToArray(),
    });
}
