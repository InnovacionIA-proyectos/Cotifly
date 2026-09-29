using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Margen sobre el costeo de Nexo y "Mis cotizaciones" (HU-005.7, ADR-015 §5) por HTTP con SQL Server real y el Nexo falso: el
/// precio sale del módulo de Precio, el costo bruto de Nexo nunca llega al comercial, los servicios conexos se eligen y el PDF
/// lista solo los incluidos.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenCosteoEndpointsTests : IDisposable
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsMargenCosteoEndpointsTests(clsEntornoApiWeb entorno) => _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElCostoBrutoDeNexoNuncaSeMuestraComoPrecioFinalAlComercial()
    {
        var (sesion, folio) = await EnviarAsync();

        // Antes del costeo: sin precio (nunca el costo).
        var antes = await FilaAsync(sesion, folio);
        antes.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);

        // Nexo costea con 2.550.000 (flete + retorno) y un conexo de 170.000; la línea Importacion tiene política 15 %.
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_000_000m, retorno: 550_000m);
        _nexo.AgregarConexo(_nexo.SolicitudesCreadas[^1], "Cargue", 170_000m);
        await _api.ProcesarPollingAsync(folio);

        using var lista = await _api.EnviarAsync(_get, "/web/mis-cotizaciones", sesion.Access);
        var cuerpoLista = await lista.Content.ReadAsStringAsync(Ct);
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        var cuerpoCosteo = json.GetRawText();

        var fila = (await FilaAsync(sesion, folio));
        fila.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        fila.GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m, "2.550.000 / (1 - 0,15), del módulo de Precio");
        json.GetProperty("precio_final").GetDecimal().Should().Be(3_000_000m);
        json.GetProperty("servicios_conexos")[0].GetProperty("precio").GetDecimal().Should().Be(200_000m);
        foreach (var cuerpo in new[] { cuerpoLista, cuerpoCosteo })
        {
            cuerpo.Should().NotContain("2550000").And.NotContain("170000", "el costo de Nexo no se expone");
            cuerpo.Should().NotContain("\"costo\"");
        }
    }

    [Fact]
    public async Task ElComercialEligeLosServiciosConexosYElPdfSoloListaLosIncluidos()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        _nexo.AgregarConexo(_nexo.SolicitudesCreadas[^1], "Cargue en puerto", 170_000m);
        _nexo.AgregarConexo(_nexo.SolicitudesCreadas[^1], "Escolta", 85_000m, "adicional");
        await _api.ProcesarPollingAsync(folio);
        using var antes = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var servicios = (await VerificarContratoAsync(antes, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("servicios_conexos").EnumerateArray().ToList();
        servicios.Should().OnlyContain(s => s.GetProperty("incluido").GetBoolean(), "por defecto se incluyen");
        var cargue = servicios.Single(s => s.GetProperty("nombre").GetString() == "Cargue en puerto").GetProperty("id").GetString()!;

        using var seleccion = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { incluidos = new[] { cargue } }));
        var elegida = await VerificarContratoAsync(seleccion, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _put, HttpStatusCode.OK);
        elegida.GetProperty("servicios_conexos").EnumerateArray().Count(s => s.GetProperty("incluido").GetBoolean()).Should().Be(1);

        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        var salida = await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        salida.GetProperty("folio").GetString().Should().Be(folio);

        using var descarga = await _api.EnviarAsync(_get, $"/pdfs/{folio}.pdf", sesion.Access);
        descarga.StatusCode.Should().Be(HttpStatusCode.OK);
        var texto = clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(Ct)).Texto;
        texto.Should().Contain("Cargue en puerto").And.Contain("3.000.000").And.NotContain("Escolta", "solo los conexos incluidos");
        texto.Should().NotContain("2.550.000", "el PDF lleva el precio con margen, no el costo de Nexo");

        using var otra = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        otra.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(otra, "PDF_YA_GENERADO");
    }

    [Fact]
    public async Task SinCosteoNoSePuedenElegirConexosNiGenerarElPdfYUnaAjenaEs404()
    {
        var (sesion, folio) = await EnviarAsync();
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        using var seleccion = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access, """{"incluidos":[]}""");
        using var ajena = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", luis.Access);

        pdf.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(pdf, "COSTEO_NO_LISTO");
        seleccion.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        json.GetProperty("origen_margen").GetString().Should().Be("politica_linea", "el margen se resuelve al enviar (sin SAP TM configurado)");
        json.GetProperty("margen_aplicado").GetDecimal().Should().Be(0.15m);
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    private async Task<JsonElement> FilaAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, "/web/mis-cotizaciones", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/mis-cotizaciones", _get, HttpStatusCode.OK);
        return json.GetProperty("cotizaciones").EnumerateArray().Single(c => c.GetProperty("folio").GetString() == folio);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
