using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-009 (#139, B1): SAP TM propone un margen de 75 % (fuera de [0, 60 %], "anómalo"). Ese margen no se aplica a ciegas: la ruta usa el
/// de la política de su línea, el de SAP queda guardado como marca (<c>margenSAPSugerido</c>) y <c>GET .../costeo</c> lo avisa. Vale en la
/// captura, en la versión nueva (<c>-vN</c>, re-solicitud) y en la licitación, que comparten la resolución del margen al enviar a Nexo.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenSapAnomaloEndpointsTests : IDisposable
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsMargenSapAnomaloEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo,
            servicios: s => s.AddTransient<IClsFuenteMargen>(_ => new clsFuenteMargenFija(75m)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnMargenSapDe75NoSeAplicaLaRutaUsaLaPoliticaYQuedaAvisadoTambienEnLaVersionNueva()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        // Captura (Importacion, política 15 %): SAP TM propone 75 %.
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        (await MargenRutasAsync(folio)).Should().Equal(["1|politica_linea|0.1500|0.7500"], "se aplica la política y el 75 % queda como marca");

        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.Costear(solicitud, 1_000_000m);
        await _api.ProcesarPollingAsync(folio);

        var ruta = await RutaAsync(ana, folio);
        ruta.GetProperty("origen_margen").GetString().Should().Be("politica_linea");
        ruta.GetProperty("margen_aplicado").GetDecimal().Should().Be(0.15m);
        ruta.GetProperty("margen_sugerido").GetDecimal().Should().Be(0.15m, "el sugerido que ve el comercial es el de la política");
        ruta.GetProperty("margen_sap_anomalo").GetBoolean().Should().BeTrue();
        ruta.GetProperty("aviso_margen").GetString().Should().Contain("75 %").And.Contain("política");
        ruta.GetProperty("precio_final_linea").GetDecimal().Should().Be(1_176_471m, "1.000.000 / 0,85, no / 0,25");

        // Versión nueva (-vN): pasa otra vez por la resolución del margen al enviar y conserva la marca.
        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);
        using var reSolicitud = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            clsApiWebPruebas.Json(new { motivo = "Actualizar el costo", nueva_version = true }), await EtagAsync(ana, folio));
        var folioNuevo = (await VerificarContratoAsync(reSolicitud, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, HttpStatusCode.Created))
            .GetProperty("folio").GetString()!;
        folioNuevo.Should().Be(folio + "-v2");
        (await MargenRutasAsync(folioNuevo)).Should().Equal(["1|politica_linea|0.1500|0.7500"]);
    }

    [Fact]
    public async Task LaReSolicitudEnElMismoFolioVuelveAResolverElMargenYConservaLaMarca()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.Costear(solicitud, 1_000_000m);
        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);

        // Entregada y sin enviar al cliente: se re-solicita en el MISMO folio (otra solicitud de Nexo, el margen se resuelve otra vez).
        using var reSolicitud = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", ana.Access,
            clsApiWebPruebas.Json(new { motivo = "El cliente pidió actualizar el costo" }), await EtagAsync(ana, folio));
        var json = await VerificarContratoAsync(reSolicitud, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, HttpStatusCode.OK);

        json.GetProperty("folio").GetString().Should().Be(folio, "mismo folio, sin versión nueva");
        _nexo.SolicitudesCreadas[^1].Should().NotBe(solicitud);
        (await MargenRutasAsync(folio)).Should().Equal(["1|politica_linea|0.1500|0.7500"]);
    }

    [Fact]
    public async Task EnLaLicitacionCadaRutaConMargenSapAnomaloUsaLaPolitica()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var alta = await _api.EnviarContenidoAsync(_post, "/web/licitaciones", ana.Access,
            clsApiWebPruebas.Licitacion(clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, clsPliegoMuestra.Lineas()), clsPliegoMuestra.Excel()));
        var folio = (await VerificarContratoAsync(alta, "/web/licitaciones", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        (await MargenRutasAsync(folio)).Should().Equal(
            "1|politica_linea|0.1500|0.7500", "2|politica_linea|0.1500|0.7500", "3|politica_linea|0.1500|0.7500");
    }

    /// <summary>
    /// Bordes que redondea la columna <c>decimal(9,4)</c> (#139): el margen de SAP se redondea a 4 decimales ANTES de evaluar la anomalía,
    /// así lo evaluado y lo guardado coinciden: 60,004 % → 0,6000 (60 %, válido) y -0,004 % → 0 (válido), los dos aplicados como SAP TM.
    /// </summary>
    [Theory]
    [InlineData("60.004", "0.6000")]
    [InlineData("-0.004", "0.0000")]
    public async Task UnMargenSapQueRedondeaDentroDelRangoSeAplicaSinMarca(string valorSap, string guardado)
    {
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, servicios: s => s.AddTransient<IClsFuenteMargen>(
            _ => new clsFuenteMargenFija(decimal.Parse(valorSap, System.Globalization.CultureInfo.InvariantCulture))));
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        (await MargenRutasAsync(folio)).Should().Equal([$"1|sap_tm|{guardado}|{guardado}"], "lo evaluado (4 decimales) es lo guardado");
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var ruta = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("rutas").EnumerateArray().Should().ContainSingle().Subject;
        ruta.GetProperty("margen_sap_anomalo").GetBoolean().Should().BeFalse();
        ruta.GetProperty("aviso_margen").ValueKind.Should().Be(JsonValueKind.Null);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }

    /// <summary>Por ruta: <c>orden|origenMargen|margenAplicado|margenSAPSugerido</c>.</summary>
    private async Task<IReadOnlyList<string>> MargenRutasAsync(string folio) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT STRING_AGG(CONCAT(l.orden, '|', l.origenMargen, '|', l.margenAplicado, '|', l.margenSAPSugerido), ',') WITHIN GROUP (ORDER BY l.orden) "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}'"))!.Split(',');

    private async Task<JsonElement> RutaAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return json.GetProperty("rutas").EnumerateArray().Should().ContainSingle().Subject;
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        costeo.StatusCode.Should().Be(HttpStatusCode.OK, await costeo.Content.ReadAsStringAsync(Ct));
        return costeo.Headers.ETag?.Tag;
    }

    /// <summary>SAP TM configurado que responde siempre el mismo <c>Value</c> (porcentaje de SAP: 75 = 75 %).</summary>
    private sealed class clsFuenteMargenFija(decimal valor) : IClsFuenteMargen
    {
        public string Modo => "fija";

        public bool EstaConfigurada => true;

        public Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(valor);
    }
}
