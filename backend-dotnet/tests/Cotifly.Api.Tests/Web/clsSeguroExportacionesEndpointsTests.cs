using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-045.3 (#265, ADR-029 §9) por HTTP con SQL Server real y el Nexo falso: el detalle por folio cuadra el desglose con
/// <c>totales.precio_seguro</c>, el Excel trae el seguro en su columna, el PDF sale, y una ruta con el seguro marcado sin valor de la
/// mercancía trae <c>aviso_seguro</c> en el costeo y en la respuesta del PDF (no bloquea). El formulario de prueba trae
/// <c>valor_mercancia = 250.000.000</c>: el seguro es 862.500.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSeguroExportacionesEndpointsTests : IDisposable
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;
    private readonly clsEntornoApiWeb _entorno;

    public clsSeguroExportacionesEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElDetalleCuadraElDesgloseConElSeguroYElExcelYElPdfLoTraen()
    {
        var (sesion, folio) = await ConSeguroAsync(quitarValor: false);
        await _api.FinalizarAsync(sesion.Access, folio);

        using var detalle = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}", sesion.Access);
        var totales = (await VerificarContratoAsync(detalle, "/web/cotizaciones/{folio}", _get, HttpStatusCode.OK)).GetProperty("totales");
        totales.GetProperty("precio_seguro").GetDecimal().Should().Be(862_500m);
        totales.GetProperty("precio_transporte").GetDecimal().Should().Be(3_000_000m, "2.550.000 / 0,85, sin el seguro");
        (totales.GetProperty("precio_transporte").GetDecimal() + totales.GetProperty("precio_conexos").GetDecimal()
         + totales.GetProperty("precio_seguro").GetDecimal()).Should().Be(totales.GetProperty("precio_final").GetDecimal(), "B4: el desglose cuadra");

        using var excel = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/excel", sesion.Access);
        excel.StatusCode.Should().Be(HttpStatusCode.OK);
        using var libro = new XLWorkbook(new MemoryStream(await excel.Content.ReadAsByteArrayAsync(Ct)));
        var rutas = libro.Worksheet("Rutas");
        var columna = rutas.Row(1).CellsUsed().Single(c => c.GetString() == "Seguro de la mercancía (COP)").Address.ColumnNumber;
        rutas.Cell(2, columna).GetValue<decimal>().Should().Be(862_500m);

        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
    }

    /// <summary>Decisión del usuario del 2026-11-09: no bloquea; el costeo y la respuesta del PDF traen <c>aviso_seguro</c>.</summary>
    [Fact]
    public async Task ConElSeguroMarcadoSinValorDeLaMercanciaElPdfSaleConElAviso()
    {
        var (sesion, folio) = await ConSeguroAsync(quitarValor: true);
        await _api.FinalizarAsync(sesion.Access, folio);

        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var vista = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        vista.GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m, "sin valor, el seguro no suma");
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);

        var salida = await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        foreach (var aviso in new[] { vista.GetProperty("aviso_seguro"), salida.GetProperty("aviso_seguro") })
        {
            aviso.GetProperty("codigo").GetString().Should().Be("SEGURO_SIN_VALOR_MERCANCIA");
            var ruta = aviso.GetProperty("rutas").EnumerateArray().Single();
            ruta.GetProperty("linea_orden").GetInt32().Should().Be(1);
            ruta.GetProperty("linea_solicitud_id").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task ConElSeguroCalculadoNoHayAviso()
    {
        var (sesion, folio) = await ConSeguroAsync(quitarValor: false);

        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);

        (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("aviso_seguro").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// Formulario con el seguro marcado (antes del costeo) y costeado (flete 2.550.000 al 15 %). Con <paramref name="quitarValor"/> la ruta
    /// queda como la deja una edición sin valor de la mercancía: el check marcado sin tasa ni costo (el script 20 lo admite).
    /// </summary>
    private async Task<(clsSesionPrueba Sesion, string Folio)> ConSeguroAsync(bool quitarValor)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var alta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(alta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        using var seguro = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/seguro", sesion.Access, """{"incluye_seguro":true}""",
            costeo.Headers.ETag?.Tag);
        seguro.StatusCode.Should().Be(HttpStatusCode.OK, await seguro.Content.ReadAsStringAsync(Ct));
        if (quitarValor)
        {
            (await EscalarAsync<int>("UPDATE l SET valorMercancia = NULL, tasaSeguroAplicada = NULL, costoSeguro = NULL "
                + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
                + $"WHERE c.folio = '{folio}'; SELECT @@ROWCOUNT")).Should().Be(1);
        }

        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    private Task<T?> EscalarAsync<T>(string sql) => clsBaseDatosPruebas.EscalarAsync<T>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
