using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-034.1 (#201, parte A): <c>GET /web/licitaciones/{folio}</c> y <c>GET /web/licitaciones/{folio}/excel</c> para el gerente comercial de
/// la regional del dueño y para el director (cualquier regional), en solo lectura y sin costos, por HTTP contra SQL Server real y el Nexo
/// falso. La regla es la del detalle de la cotización y del PDF: 404 fail-closed para cualquier otro y auditoría de cada intento gerencial.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsLicitacionSoloLecturaEndpointsTests : IDisposable
{
    private const string Vista = "/web/licitaciones/{folio}";
    private const string NoEsTuya = """{"error":"Cotización no encontrada o no es tuya."}""";
    private const decimal Flete = 1_000_007m;
    private const decimal CostoConexo = 170_007m;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsLicitacionSoloLecturaEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Regional() => $"Lic-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task ElGerenteDeLaRegionalYElDirectorVenLaLicitacionEnSoloLecturaYLosDemasReciben404Auditado()
    {
        var occidente = Regional();
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Ana Occidente");
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var hugo = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());
        var sinRegional = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var dario = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoDirector, Regional());
        var folio = await LicitacionCosteadaAsync(ana);

        using var dueno = await Get(folio, ana);
        using var gerente = await Get(folio, gloria);
        using var director = await Get(folio, dario);
        using var otraRegional = await Get(folio, hugo);
        using var gerenteSinRegional = await Get(folio, sinRegional);
        using var ajeno = await Get(folio, luis);
        using var inexistente = await Get("PCN-XX-01011999-999", gloria);

        var deDueno = await VerificarContratoAsync(dueno, Vista, HttpMethod.Get, HttpStatusCode.OK);
        deDueno.GetProperty("solo_lectura").GetBoolean().Should().BeFalse();
        foreach (var permitida in new[] { gerente, director })
        {
            var vista = await VerificarContratoAsync(permitida, Vista, HttpMethod.Get, HttpStatusCode.OK);
            vista.GetProperty("solo_lectura").GetBoolean().Should().BeTrue();
            SinSoloLectura(vista).Should().Be(SinSoloLectura(deDueno), "ven la misma licitación que el dueño, salvo solo_lectura");
            vista.GetRawText().Should().NotContain("1000007").And.NotContain("170007").And.NotContain("costo");
        }

        foreach (var negada in new[] { otraRegional, gerenteSinRegional, ajeno, inexistente })
        {
            await VerificarContratoAsync(negada, Vista, HttpMethod.Get, HttpStatusCode.NotFound);
            (await negada.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya, "igual que si no existiera");
        }

        (await AuditoriaAsync("consultar_licitacion", folio, "1"))
            .Should().Be(2, "gloria y dario (permitidas)");
        (await AuditoriaAsync("consultar_licitacion", folio, "0"))
            .Should().Be(2, "hugo y el gerente sin regional (negadas); ni el dueño ni el comercial ajeno");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'consultar_licitacion' AND input LIKE '%{folio}%' AND outputResumen LIKE '%REGIONAL_NO_ASIGNADA%'"))
            .Should().Be(1, "el gerente sin regional queda con el motivo");
    }

    [Fact]
    public async Task ElGerenteDeLaRegionalYElDirectorBajanElMismoExcelDelDuenoSinCostosYLosDemasReciben404()
    {
        var occidente = Regional();
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var hugo = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());
        var sinRegional = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var dario = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoDirector, Regional());
        var folio = await LicitacionCosteadaAsync(ana);

        var deDueno = await ExcelAsync(folio, ana);
        foreach (var sesion in new[] { gloria, dario })
        {
            var excel = await ExcelAsync(folio, sesion);
            Celdas(excel).Should().Equal(Celdas(deDueno), "es el mismo archivo del dueño");
            SinCostos(excel);
        }

        foreach (var sesion in new[] { hugo, sinRegional, luis })
        {
            using var negada = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}/excel", sesion.Access);
            negada.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await negada.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya);
        }

        (await AuditoriaAsync("exportar_excel_licitacion", folio, "1")).Should().Be(2, "gloria y dario");
        (await AuditoriaAsync("exportar_excel_licitacion", folio, "0")).Should().Be(2, "hugo y el gerente sin regional; no el comercial ajeno");
    }

    [Fact]
    public async Task ElGerenteYElDirectorNoCambianLaLicitacion()
    {
        var occidente = Regional();
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var dario = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoDirector, Regional());
        var folio = await LicitacionCosteadaAsync(ana);

        foreach (var sesion in new[] { gloria, dario })
        {
            using var respaldo = await _api.EnviarAsync(
                HttpMethod.Post, $"/web/licitaciones/{folio}/cubicacion/reintentar", sesion.Access, """{"usar_respaldo":true}""");
            using var reintento = await _api.EnviarAsync(HttpMethod.Post, $"/web/licitaciones/{folio}/cubicacion/reintentar", sesion.Access);
            using var envio = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);
            // Con el If-Match vigente (el del detalle, que sí puede ver): el 404 es por no ser el dueño, no por la precondición.
            using var detalle = await _api.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}", sesion.Access);
            detalle.StatusCode.Should().Be(HttpStatusCode.OK);
            using var finalizar = await _api.EnviarAsync(
                HttpMethod.Post, $"/web/cotizaciones/{folio}/finalizar", sesion.Access, ifMatch: detalle.Headers.ETag!.Tag);
            foreach (var mutacion in new[] { respaldo, reintento, envio, finalizar })
            {
                mutacion.StatusCode.Should().Be(HttpStatusCode.NotFound, mutacion.RequestMessage!.RequestUri!.AbsolutePath);
            }
        }

        (await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
                $"SELECT CONCAT(estadoCosteo, '|', IIF(finalizadaAt IS NULL, 'abierta', 'finalizada')) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
            .Should().Be("pendiente_costeo|abierta", "nada cambió");
    }

    /// <summary>Licitación de 3 rutas con la 1 costeada (flete <see cref="Flete"/>) y un conexo con costo <see cref="CostoConexo"/>.</summary>
    private async Task<string> LicitacionCosteadaAsync(clsSesionPrueba sesion)
    {
        using var respuesta = await _api.EnviarContenidoAsync(HttpMethod.Post, "/web/licitaciones", sesion.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, clsPliegoMuestra.Lineas()), clsPliegoMuestra.Excel()));
        var folio = (await VerificarContratoAsync(respuesta, "/web/licitaciones", HttpMethod.Post, HttpStatusCode.Created))
            .GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, Flete);
        _nexo.AgregarConexo(solicitud, "Cargue", CostoConexo);
        await _api.ProcesarPollingAsync(folio);
        return folio;
    }

    private Task<HttpResponseMessage> Get(string folio, clsSesionPrueba sesion) =>
        _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{Uri.EscapeDataString(folio)}", sesion.Access);

    private async Task<byte[]> ExcelAsync(string folio, clsSesionPrueba sesion)
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{folio}/excel", sesion.Access);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        respuesta.Content.Headers.ContentDisposition!.FileNameStar.Should().Be($"{folio}.xlsx");
        return await respuesta.Content.ReadAsByteArrayAsync(Ct);
    }

    private Task<int> AuditoriaAsync(string herramienta, string folio, string exito) =>
        clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{herramienta}' AND input LIKE '%{folio}%' AND exito = {exito}");

    /// <summary>La vista serializada sin <c>solo_lectura</c> (el único campo que cambia entre el dueño y quien solo consulta).</summary>
    private static string SinSoloLectura(JsonElement vista)
    {
        var nodo = JsonNode.Parse(vista.GetRawText())!.AsObject();
        nodo.Remove("solo_lectura");
        return nodo.ToJsonString();
    }

    /// <summary>Todas las celdas usadas del libro (hoja!celda=texto), sin la fila "Generado" del resumen (la hora de la descarga).</summary>
    private static List<string> Celdas(byte[] excel)
    {
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(excel));
        return [.. libro.Worksheets.SelectMany(h => h.CellsUsed()
            .Where(c => !string.Equals(c.WorksheetRow().Cell(1).GetString(), "Generado", StringComparison.Ordinal))
            .Select(c => $"{h.Name}!{c.Address}={c.GetFormattedString()}"))];
    }

    private static void SinCostos(byte[] excel)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(excel));
        foreach (var entrada in zip.Entries)
        {
            using var lector = new StreamReader(entrada.Open());
            var texto = lector.ReadToEnd();
            texto.Should().NotContain("1000007", entrada.FullName).And.NotContain("170007", entrada.FullName);
        }
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
