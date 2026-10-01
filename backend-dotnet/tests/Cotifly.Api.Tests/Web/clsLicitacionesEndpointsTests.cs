using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Licitaciones desde Excel (HU-011.2, ADR-017) por HTTP contra SQL Server real y un Nexo falso: análisis reenviado a Nexo, alta
/// multipart o JSON, reporte por fila, una sola solicitud con N líneas, polling línea por línea y la vista sin costos.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsLicitacionesEndpointsTests : IDisposable
{
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsLicitacionesEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task ElAnalisisReenviaElArchivoANexoYCumpleElContrato()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarContenidoAsync(HttpMethod.Post, "/web/licitaciones/analizar-excel", sesion.Access,
            clsApiWebPruebas.Archivo(clsPliegoMuestra.Excel()));

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones/analizar-excel", HttpMethod.Post, HttpStatusCode.OK);
        json.GetProperty("hojas")[0].GetProperty("filas").GetArrayLength().Should().Be(3);
        _nexo.UltimoArchivoAnalizado.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(null, "ARCHIVO_REQUERIDO")]
    [InlineData("pliego.xls", "FORMATO_NO_SOPORTADO")]
    public async Task UnArchivoInvalidoSeRechazaSinLlamarANexo(string? nombre, string codigo)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        HttpContent contenido = nombre is null
            ? new MultipartFormDataContent { { new StringContent("x"), "otro" } }
            : clsApiWebPruebas.Archivo([1, 2, 3], nombre);

        using var respuesta = await _api.EnviarContenidoAsync(HttpMethod.Post, "/web/licitaciones/analizar-excel", sesion.Access, contenido);

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones/analizar-excel", HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.EndsWith("analizar-excel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnSinPermisoDeNexoEsNexoSinPermisoNoUnaCaida()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AnalisisFalla = HttpStatusCode.Forbidden;
        _nexo.CodigoAnalisisFalla = "SIN_PERMISO_CABINA_FLETES";

        using var respuesta = await _api.EnviarContenidoAsync(HttpMethod.Post, "/web/licitaciones/analizar-excel", sesion.Access,
            clsApiWebPruebas.Archivo(clsPliegoMuestra.Excel()));

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones/analizar-excel", HttpMethod.Post, HttpStatusCode.Forbidden);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_SIN_PERMISO");
    }

    [Fact]
    public async Task LaLicitacionConArchivoGuardaSusLineasYElOriginalYViajaComoUnaSolicitudDeNLineas()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var excel = clsPliegoMuestra.Excel();

        var json = await CrearAsync(sesion, clsPliegoMuestra.Lineas(), excel, HttpStatusCode.Created);

        var folio = json.GetProperty("folio").GetString()!;
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        json.GetProperty("lineas").GetArrayLength().Should().Be(3);
        json.GetProperty("archivo_nombre").GetString().Should().Be("pliego.xlsx");
        var alta = _nexo.UltimaAlta!;
        alta["tipo"]!.GetValue<string>().Should().Be("licitacion");
        alta["lineas"]!.AsArray().Select(l => l!["origen"]!.GetValue<string>()).Should().Equal("BUENAVENTURA", "CALI", "CUCUTA");
        (await EscalarAsync($"SELECT CONCAT(COUNT(*), '|', MIN(l.estadoCosteo), '|', SUM(l.filaOrigen)) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.Cotizaciones c ON c.id = l.cotizacionId WHERE c.folio = '{folio}'"))
            .Should().Be("3|pendiente_costeo|3");
        (await EscalarAsync($"SELECT CONCAT(c.tipoSolicitud, '|', ISNULL(c.origen, 'sin-linea'), '|', a.tamanoBytes, '|', a.hoja) FROM dbo_cotifly.Cotizaciones c JOIN dbo_cotifly.CotizacionArchivosLicitacion a ON a.cotizacionId = c.id WHERE c.folio = '{folio}'"))
            .Should().Be($"licitacion|sin-linea|{excel.Length}|{clsPliegoMuestra.Hoja}", "una licitación no llena las columnas de línea obsoletas de Cotizaciones");
    }

    [Fact]
    public async Task SinArchivoLaLicitacionSeCreaConJson()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object[] lineas = [new { origen = "YUMBO", destino = "CALI", peso_kg = 1000 }, new { origen = "CALI", destino = "YUMBO", peso_kg = 2000 }];

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, "/web/licitaciones", sesion.Access, clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, lineas, hoja: null));

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones", HttpMethod.Post, HttpStatusCode.Created);
        json.GetProperty("archivo_nombre").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("lineas").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task LasFilasInvalidasSeReportanTodasYNadaQuedaGuardado()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object[] lineas =
        [
            new { fila = 0, origen = "BUENAVENTURA", destino = "", peso_kg = 32000 },
            new { fila = 1, origen = "CALI", destino = "CARTAGENA", peso_kg = 28000, valor_mercancia = 1.234 },
            new { fila = 7, origen = "CUCUTA", destino = "BOGOTA, D.C.", peso_kg = 8000 },
        ];

        var json = await CrearAsync(sesion, lineas, clsPliegoMuestra.Excel(), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("LICITACION_INVALIDA");
        json.GetProperty("errores").EnumerateArray().Select(e => (e.GetProperty("indice").GetInt32(), e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString()))
            .Should().BeEquivalentTo(new[] { (0, "destino", "LINEA_INCOMPLETA"), (1, "valor_mercancia", "VALOR_FUERA_DE_RANGO"), (2, "fila", "FILA_INVALIDA") });
        _nexo.SolicitudesCreadas.Should().BeEmpty();
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.Cotizaciones WHERE usuarioId = '{sesion.UsuarioId}'")).Should().Be("0");
    }

    [Fact]
    public async Task UnaHojaQueNoEstaEnElArchivoEsExcelSinDatos()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarContenidoAsync(HttpMethod.Post, "/web/licitaciones", sesion.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, clsPliegoMuestra.Lineas(), hoja: "No existe"), clsPliegoMuestra.Excel()));

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones", HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("EXCEL_SIN_DATOS");
    }

    [Fact]
    public async Task ElPollingCosteaLineaPorLineaYLaVistaNuncaTraeElCosto()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(sesion, clsPliegoMuestra.Lineas(), clsPliegoMuestra.Excel(), HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 2, 850_000m);

        await _api.ProcesarPollingAsync(folio);

        var parcial = await VistaAsync(sesion, folio);
        parcial.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        parcial.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        parcial.GetProperty("lineas").EnumerateArray().Select(l => l.GetProperty("precio_final").ValueKind == JsonValueKind.Null ? (decimal?)null : l.GetProperty("precio_final").GetDecimal())
            .Should().Equal(1_176_471m, null, 1_000_000m);
        (await EscalarAsync($"SELECT STRING_AGG(l.lineaSolicitudNexoId, ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.Cotizaciones c ON c.id = l.cotizacionId WHERE c.folio = '{folio}'"))
            .Should().Be(string.Join(',', _nexo.LineasDe(solicitud)), "los ids de Nexo se asignan por orden");

        _nexo.CostearLinea(solicitud, 1, 2_000_000m);
        await _api.ProcesarPollingAsync(folio);

        var completa = await VistaAsync(sesion, folio);
        completa.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        completa.GetProperty("precio_final").GetDecimal().Should().Be(1_176_471m + 2_352_941m + 1_000_000m);
        completa.GetProperty("lineas_pendientes").GetInt32().Should().Be(0);
        completa.GetRawText().Should().NotContain("850000").And.NotContain("costo");
    }

    [Fact]
    public async Task ConMasDeDiezLineasNexoCosteaEnSegundoPlanoYElPollingLasSigue()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AutoCostear = true;

        var json = await CrearAsync(sesion, clsPliegoMuestra.Lineas(12), clsPliegoMuestra.Excel(12), HttpStatusCode.Created);
        var folio = json.GetProperty("folio").GetString()!;
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo", "Nexo difiere el auto-costeo de más de 10 líneas");

        await _api.ProcesarPollingAsync(folio);

        var vista = await VistaAsync(sesion, folio);
        vista.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        vista.GetProperty("lineas_costeadas").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task LaLicitacionDeOtroComercialNoSeVe()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(ana, clsPliegoMuestra.Lineas(), clsPliegoMuestra.Excel(), HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}", luis.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ElFormularioDeUnaLineaTambienGuardaSuLineaYElPrecioDeLaLineaEsElDeLaCotizacion()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", HttpMethod.Post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);

        await _api.ProcesarPollingAsync(folio);

        (await EscalarAsync($"SELECT CONCAT(COUNT(*), '|', MAX(l.estadoCosteo), '|', IIF(MAX(l.precioFinal) = MAX(c.precioFinal), 'igual', 'distinto'), '|', IIF(MAX(c.origen) = MAX(l.origen), 'espejo', 'no')) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.Cotizaciones c ON c.id = l.cotizacionId WHERE c.folio = '{folio}'"))
            .Should().Be("1|autocosteo|igual|espejo");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CotizacionPosiciones p JOIN dbo_cotifly.Cotizaciones c ON c.id = p.cotizacionId WHERE c.folio = '{folio}' AND p.lineaSolicitudId IS NULL"))
            .Should().Be("0", "las posiciones quedan enlazadas a su línea");
    }

    [Fact]
    public async Task LaExportacionDevuelveElOriginalConLosPreciosMarcaLasPendientesYNuncaTraeElCostoDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(sesion, clsPliegoMuestra.Lineas(), clsPliegoMuestra.Excel(), HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.AgregarConexo(solicitud, "Cargue", 170_000m);
        await _api.ProcesarPollingAsync(folio);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}/excel", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        respuesta.Content.Headers.ContentDisposition!.FileNameStar.Should().Be($"{folio}.xlsx");
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
        var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(clsPliegoMuestra.FilaExcel(0), 2).GetString().Should().Be("BUENAVENTURA", "las columnas del cliente no cambian");
        hoja.Cell(clsPliegoMuestra.FilaExcel(0), 7).GetValue<decimal>().Should().Be(1_176_471m);
        hoja.Cell(clsPliegoMuestra.FilaExcel(1), 7).GetString().Should().Be("SIN COSTEO");
        hoja.Cell(clsPliegoMuestra.FilaExcel(1), 8).GetString().Should().Be("Pendiente de costeo");
        var numeros = libro.Worksheets.SelectMany(h => h.CellsUsed()).Where(c => c.DataType == ClosedXML.Excel.XLDataType.Number).Select(c => c.GetValue<decimal>()).ToList();
        numeros.Should().NotContain(1_000_000m, "el flete de Nexo nunca sale").And.NotContain(170_000m, "el costo del conexo tampoco");
    }

    [Fact]
    public async Task LaExportacionEsSoloDelDuenoYSoloDeCotizacionesConLineas()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(ana, clsPliegoMuestra.Lineas(), clsPliegoMuestra.Excel(), HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var delChat = await _api.CrearCotizacionAsync(ana.UsuarioId, conPdf: false);

        using var formulario = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var spot = (await VerificarContratoAsync(formulario, "/web/solicitudes-costeo", HttpMethod.Post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var ajena = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}/excel", luis.Access);
        using var sinLineas = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{delChat.Folio}/excel", ana.Access);
        using var noLicitacion = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{spot}/excel", ana.Access);

        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        sinLineas.StatusCode.Should().Be(HttpStatusCode.NotFound);
        noLicitacion.StatusCode.Should().Be(HttpStatusCode.NotFound, "una cotización spot de una línea no es una licitación");
    }

    [Fact]
    public async Task NingunaParteDelExcelExportadoTraeCostosDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(sesion, clsPliegoMuestra.Lineas(), clsPliegoMuestra.Excel(), HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_003m);
        _nexo.CostearLinea(solicitud, 1, 2_000_003m);
        _nexo.CostearLinea(solicitud, 2, 850_003m);
        _nexo.AgregarConexo(solicitud, "Cargue", 170_003m);
        await _api.ProcesarPollingAsync(folio);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}/excel", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
        foreach (var entrada in zip.Entries)
        {
            using var lector = new StreamReader(entrada.Open());
            var texto = await lector.ReadToEndAsync(TestContext.Current.CancellationToken);
            foreach (var costo in new[] { "1000003", "2000003", "850003", "170003" })
            {
                texto.Should().NotContain(costo, entrada.FullName);
            }
        }
    }

    [Fact]
    public async Task ElPdfDeUnaLicitacionCosteadaSaleConElLayoutDePliegoYSinCostosDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(sesion, clsPliegoMuestra.Lineas(), clsPliegoMuestra.Excel(), HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];

        using (var temprano = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access))
        {
            temprano.StatusCode.Should().Be(HttpStatusCode.Conflict, "sin todas las líneas costeadas no hay PDF del pliego");
        }

        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 2_000_000m);
        _nexo.CostearLinea(solicitud, 2, 850_000m);
        await _api.ProcesarPollingAsync(folio);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/costeo/pdf", HttpMethod.Post, HttpStatusCode.Created);
        var ruta = new Uri(new Uri("http://localhost"), json.GetProperty("pdf_url").GetString()!).AbsolutePath;
        using var descarga = await _api.EnviarAsync(HttpMethod.Get, ruta, sesion.Access);
        descarga.StatusCode.Should().Be(HttpStatusCode.OK);
        var texto = clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Texto;

        texto.Should().Contain("RESUMEN DEL PLIEGO").And.Contain("TOTAL DEL PLIEGO (3 RUTAS, SIN SERVICIOS CONEXOS)").And.Contain("$4.529.412");
        texto.Should().Contain("CARTAGENA").And.Contain("$1.176.471").And.Contain("$2.352.941").And.Contain("$1.000.000");
        texto.Should().NotContain("$850.000").And.NotContain("$2.000.000", "los fletes de Nexo nunca van al PDF");
    }

    [Fact]
    public async Task UnaLicitacionDeUnaLineaTraeLaRutaEnElPdf()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var folio = (await CrearAsync(sesion, clsPliegoMuestra.Lineas(1), clsPliegoMuestra.Excel(1), HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.CostearLinea(_nexo.SolicitudesCreadas[^1], 0, 1_000_003m);
        await _api.ProcesarPollingAsync(folio);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/costeo/pdf", HttpMethod.Post, HttpStatusCode.Created);
        var ruta = new Uri(new Uri("http://localhost"), json.GetProperty("pdf_url").GetString()!).AbsolutePath;
        using var descarga = await _api.EnviarAsync(HttpMethod.Get, ruta, sesion.Access);
        var texto = clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Texto;

        texto.Should().Contain("BUENAVENTURA").And.Contain("BOGOTA").And.Contain("$1.176.474", "la licitación de una línea sale como pliego con su ruta");
        texto.Should().NotContain("$1.000.003", "el flete de Nexo nunca va al PDF");
    }

    private async Task<JsonElement> CrearAsync(clsSesionPrueba sesion, object[] lineas, byte[] excel, HttpStatusCode esperado)
    {
        using var respuesta = await _api.EnviarContenidoAsync(HttpMethod.Post, "/web/licitaciones", sesion.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, lineas), excel));
        return await VerificarContratoAsync(respuesta, "/web/licitaciones", HttpMethod.Post, esperado);
    }

    private async Task<JsonElement> VistaAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}", sesion.Access);
        return await VerificarContratoAsync(respuesta, "/web/licitaciones/{folio}", HttpMethod.Get, HttpStatusCode.OK);
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
