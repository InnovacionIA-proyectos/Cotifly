using System.Net;
using ClosedXML.Excel;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Por HTTP contra SQL Server real: la lista de motivos de pérdida (<c>GET /web/motivos-perdida</c>, configurable
/// con <c>Resultado:MotivosPerdida</c>) y su validación en <c>/seguimiento</c>, y el Excel de la cotización
/// (<c>GET /web/cotizaciones/{folio}/excel</c>): el dueño con costos y márgenes, el gerente de su regional sin ninguno (ni en el paquete),
/// los demás 404 y 409 sin precio. Cada respuesta JSON se valida contra <c>specs/openapi.yaml</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMotivoPerdidaYExcelEndpointsTests(clsEntornoApiWeb entorno)
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;

    private clsApiWebPruebas Api => entorno.Api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Regional() => $"Mot-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task LaListaDeMotivosEsLaDeNexoYLaConfiguracionLaReemplaza()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var porDefecto = await Api.EnviarAsync(_get, "/web/motivos-perdida", ana.Access);
        var json = await VerificarContratoAsync(porDefecto, "/web/motivos-perdida", _get, HttpStatusCode.OK);
        json.GetProperty("motivos").EnumerateArray().Select(m => m.GetProperty("motivo").GetString()).Should().Equal(
            "Valor muy alto", "Flete fuera de rango de plaza", "Cliente desistió del servicio", "Tiempo de respuesta vencido", "Otro");

        using var configurada = new clsApiWebPruebas(entorno.BaseDatos, extra: new Dictionary<string, string?>
        {
            ["Resultado:MotivosPerdida:0"] = "Plazo",
            ["Resultado:MotivosPerdida:1"] = "Otra razón",
            ["Resultado:MotivoConComentario"] = "Otra razón",
        });
        var luis = await configurada.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var lista = await configurada.EnviarAsync(_get, "/web/motivos-perdida", luis.Access);
        var deConfiguracion = await VerificarContratoAsync(lista, "/web/motivos-perdida", _get, HttpStatusCode.OK);
        deConfiguracion.GetProperty("motivos").EnumerateArray()
            .Select(m => (m.GetProperty("motivo").GetString(), m.GetProperty("requiere_comentario").GetBoolean()))
            .Should().Equal(("Plazo", false), ("Otra razón", true));

        // La lista configurada también es la que valida.
        var cotizacion = await configurada.CrearCotizacionAsync(luis.UsuarioId, "900111222");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/seguimiento";
        using var deNexo = await configurada.EnviarAsync(_post, ruta, luis.Access, clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = "Valor muy alto" }));
        var error = await VerificarContratoAsync(deNexo, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.BadRequest);
        error.GetProperty("error").GetProperty("codigo").GetString().Should().Be("MOTIVO_NO_PERMITIDO");
        error.GetProperty("error").GetProperty("detalle").GetProperty("motivos_permitidos").EnumerateArray().Select(m => m.GetString())
            .Should().Equal("Plazo", "Otra razón");
        using var valido = await configurada.EnviarAsync(_post, ruta, luis.Access, clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = "plazo" }));
        (await VerificarContratoAsync(valido, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.OK))
            .GetProperty("motivo_rechazo").GetString().Should().Be("Plazo");
    }

    [Fact]
    public async Task ElSeguimientoConOtroExigeElComentarioYLoGuardaConElMotivo()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/seguimiento";

        using var sinComentario = await Api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = "Otro" }));
        var error = await VerificarContratoAsync(sinComentario, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.BadRequest);
        error.GetProperty("error").GetProperty("detalle").GetProperty("requiere_comentario").GetBoolean().Should().BeTrue();
        using var conComentario = await Api.EnviarAsync(_post, ruta, ana.Access,
            clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = "Otro", comentario = "Compró con la competencia" }));

        (await VerificarContratoAsync(conComentario, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.OK))
            .GetProperty("motivo_rechazo").GetString().Should().Be("Otro: Compró con la competencia");
    }

    [Fact]
    public async Task ElDuenoDescargaElExcelInternoYElGerenteElMismoSinCostosNiMargenes()
    {
        var occidente = Regional();
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Ana Excel");
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var hugo = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        // Ruta del chat de prueba: flete 1.000.000, margen 15 %, precio 1.176.471, con su PDF (precio congelado).
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/excel";

        using var delDueno = await Api.EnviarAsync(_get, ruta, ana.Access);
        using var delGerente = await Api.EnviarAsync(_get, ruta, gloria.Access);
        using var otraRegional = await Api.EnviarAsync(_get, ruta, hugo.Access);
        using var ajeno = await Api.EnviarAsync(_get, ruta, luis.Access);

        foreach (var respuesta in new[] { delDueno, delGerente })
        {
            respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
            respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            respuesta.Content.Headers.ContentDisposition!.FileNameStar.Should().Be($"{cotizacion.Folio}.xlsx");
        }

        var dueno = await Bytes(delDueno);
        using (var libro = new XLWorkbook(new MemoryStream(dueno)))
        {
            Numeros(libro).Should().Contain([1_000_000m, 0.15m, 1_176_471m]);
        }

        var gerente = await Bytes(delGerente);
        using (var libro = new XLWorkbook(new MemoryStream(gerente)))
        {
            Numeros(libro).Should().Contain(1_176_471m).And.NotContain([1_000_000m, 0.15m]);
        }

        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(gerente)))
        {
            foreach (var entrada in zip.Entries)
            {
                using var lector = new StreamReader(entrada.Open());
                (await lector.ReadToEndAsync(Ct)).Should().NotContain("1000000", entrada.FullName).And.NotContain("Costo", entrada.FullName);
            }
        }

        foreach (var negada in new[] { otraRegional, ajeno })
        {
            await VerificarContratoAsync(negada, "/web/cotizaciones/{folio}/excel", _get, HttpStatusCode.NotFound);
        }

        (await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'exportar_excel_cotizacion' AND input LIKE '%{cotizacion.Folio}%'"))
            .Should().Be(2, "gloria (permitida) y hugo (negado); ni el dueño ni el comercial ajeno");

        static Task<byte[]> Bytes(HttpResponseMessage r) => r.Content.ReadAsByteArrayAsync(Ct);
    }

    [Fact]
    public async Task SinPrecioElExcelEs409CosteoNoListo()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        await clsBaseDatosPruebas.EjecutarAsync(entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET precioFinal = NULL WHERE id = '{cotizacion.Id}'");

        using var respuesta = await Api.EnviarAsync(_get, $"/web/cotizaciones/{cotizacion.Folio}/excel", ana.Access);

        var error = await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/excel", _get, HttpStatusCode.Conflict);
        error.GetProperty("error").GetProperty("codigo").GetString().Should().Be("COSTEO_NO_LISTO");
    }

    private static List<decimal> Numeros(XLWorkbook libro) =>
        [.. libro.Worksheets.SelectMany(h => h.CellsUsed()).Where(c => c.DataType == XLDataType.Number).Select(c => c.GetValue<decimal>())];
}
