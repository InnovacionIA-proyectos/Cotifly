using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>GET /web/metricas/comercial</c> y <c>/regional</c> (HU-010.1, ADR-016) por HTTP contra la base real: contrato, rol del
/// token, versión vigente, USD a COP, regional del gerente y sus errores (403, 404, 409, 400).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMetricasTableroEndpointsTests(clsEntornoApiWeb entorno)
{
    private const string Periodo = "desde=2026-01-01&hasta=2026-12-31";
    private static readonly HttpMethod _get = HttpMethod.Get;

    private clsApiWebPruebas Api => entorno.Api;

    private static string Regional() => $"Reg-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task ElComercialVeSusIndicadoresConUsdEnPesosYSoloLaVersionVigente()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var otro = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cop = await CrearAsync(ana.UsuarioId, "COP", 1_000_000m, 0m, 850_000m, true, "2026-03-10");
        await CrearAsync(ana.UsuarioId, "USD", 1_000m, 4_000m, 3_400_000m, true, "2026-04-05");
        await CrearAsync(otro.UsuarioId, "COP", 9_000_000m, 0m, 1m, true, "2026-04-05");
        await NuevaVersionAsync(cop, 2, 1_200_000m, "2026-03-11");

        using var respuesta = await Api.EnviarAsync(_get, $"/web/metricas/comercial?{Periodo}", ana.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/metricas/comercial", _get, HttpStatusCode.OK);

        json.GetProperty("usuario_id").GetString().Should().Be(ana.UsuarioId.ToString());
        var indicadores = json.GetProperty("indicadores");
        indicadores.GetProperty("cantidad").GetInt32().Should().Be(2, "la v1 rehecha no cuenta");
        indicadores.GetProperty("versiones").GetInt32().Should().Be(3);
        indicadores.GetProperty("valor_ganado_cop").GetDecimal().Should().Be(5_200_000m, "1.200.000 de la v2 + 1.000 USD × 4.000");
        indicadores.GetProperty("costeo_total").GetDecimal().Should().Be(4_250_000m);
        indicadores.GetProperty("costeo_ganadas").GetDecimal().Should().Be(4_250_000m);
        indicadores.GetProperty("deadline_vencidas").GetInt32().Should().Be(0, "el 0004 ya está en QA y ninguna cotización tiene deadline");
        indicadores.GetProperty("ganancias").GetDecimal().Should().Be(950_000m);
        indicadores.GetProperty("cotizaciones_usd").GetInt32().Should().Be(1);
        json.GetProperty("serie_mensual").GetArrayLength().Should().Be(12);
    }

    [Fact]
    public async Task UnComercialSinCotizacionesRecibeCerosYPromediosNulos()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await Api.EnviarAsync(_get, "/web/metricas/comercial", ana.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/metricas/comercial", _get, HttpStatusCode.OK);

        json.GetProperty("indicadores").GetProperty("cantidad").GetInt32().Should().Be(0);
        json.GetProperty("indicadores").GetProperty("conversion").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("periodo").GetProperty("zona_horaria").GetString().Should().Be("America/Bogota");
    }

    [Fact]
    public async Task FechasInvalidasResponden400()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await Api.EnviarAsync(_get, "/web/metricas/comercial?desde=2026-02-30", ana.Access);

        await VerificarContratoAsync(respuesta, "/web/metricas/comercial", _get, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ElGerenteVeSuRegionalFiltraPorComercialYNoVeOtrasRegionales()
    {
        var occidente = Regional();
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Ana Tablero");
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Luis Tablero");
        var marta = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional(), "Marta Tablero");
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente, "Gloria Tablero");
        await CrearAsync(ana.UsuarioId, "COP", 1_000_000m, 0m, 850_000m, true, "2026-03-10");
        await CrearAsync(ana.UsuarioId, "COP", 2_000_000m, 0m, 1_700_000m, false, "2026-03-11");
        await CrearAsync(luis.UsuarioId, "COP", 500_000m, 0m, 400_000m, true, "2026-04-15");
        await CrearAsync(marta.UsuarioId, "COP", 9_000_000m, 0m, 8_000_000m, true, "2026-04-15");

        using var todos = await Api.EnviarAsync(_get, $"/web/metricas/regional?{Periodo}", gloria.Access);
        var regional = await VerificarContratoAsync(todos, "/web/metricas/regional", _get, HttpStatusCode.OK);
        using var filtrado = await Api.EnviarAsync(_get, $"/web/metricas/regional?{Periodo}&usuario_id={luis.UsuarioId}", gloria.Access);
        var deLuis = await VerificarContratoAsync(filtrado, "/web/metricas/regional", _get, HttpStatusCode.OK);
        using var ajeno = await Api.EnviarAsync(_get, $"/web/metricas/regional?{Periodo}&usuario_id={marta.UsuarioId}", gloria.Access);

        regional.GetProperty("regional").GetString().Should().Be(occidente);
        regional.GetProperty("fuente_regional").GetString().Should().Be("usuarios_web");
        regional.GetProperty("indicadores").GetProperty("cantidad").GetInt32().Should().Be(3);
        regional.GetProperty("indicadores").GetProperty("valor_ganado_cop").GetDecimal().Should().Be(1_500_000m);
        regional.GetProperty("lineas_negocio_activas").GetInt32().Should().BeGreaterThan(0);
        regional.GetProperty("concentracion_ciudades").GetProperty("disponible").GetBoolean().Should().BeTrue("el 0004 ya está en QA (origen y destino vacíos)");
        regional.GetProperty("comerciales").EnumerateArray().Select(c => c.GetProperty("nombre_completo").GetString())
            .Should().Equal("Ana Tablero", "Gloria Tablero", "Luis Tablero");
        regional.GetProperty("comerciales").EnumerateArray().Select(c => c.GetProperty("rol").GetString())
            .Should().Equal("comercial", "gerente_comercial", "comercial");
        deLuis.GetProperty("usuario_id").GetString().Should().Be(luis.UsuarioId.ToString());
        deLuis.GetProperty("indicadores").GetProperty("cantidad").GetInt32().Should().Be(1);
        deLuis.GetProperty("comerciales").GetArrayLength().Should().Be(3);
        await VerificarContratoAsync(ajeno, "/web/metricas/regional", _get, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnComercialRecibe403YUnGerenteSinRegional409()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional());
        var sinRegional = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var comercial = await Api.EnviarAsync(_get, "/web/metricas/regional", ana.Access);
        using var gerente = await Api.EnviarAsync(_get, "/web/metricas/regional", sinRegional.Access);

        await VerificarContratoAsync(comercial, "/web/metricas/regional", _get, HttpStatusCode.Forbidden);
        await VerificarContratoAsync(gerente, "/web/metricas/regional", _get, HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(gerente, "REGIONAL_NO_ASIGNADA");
    }

    [Fact]
    public async Task UnUsuarioIdQueNoEsUuidResponde400()
    {
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());

        using var respuesta = await Api.EnviarAsync(_get, "/web/metricas/regional?usuario_id=luis", gloria.Access);

        await VerificarContratoAsync(respuesta, "/web/metricas/regional", _get, HttpStatusCode.BadRequest);
    }

    /// <summary>Cotización del comercial con los valores del tablero fijados por SQL (moneda, TRM, precio, costo, resultado, fecha).</summary>
    private async Task<clsCotizacionPrueba> CrearAsync(Guid usuarioId, string moneda, decimal precio, decimal trm, decimal costo, bool? ganada, string creada)
    {
        var cotizacion = await Api.CrearCotizacionAsync(usuarioId, conPdf: false);
        await clsBaseDatosPruebas.EjecutarAsync(entorno.BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.Cotizaciones
            SET moneda = N'{moneda}', trmAplicada = {clsApiWebPruebas.Numero(trm)}, precioFinal = {clsApiWebPruebas.Numero(precio)},
                costo = {clsApiWebPruebas.Numero(costo)}, ganada = {(ganada is null ? "NULL" : ganada.Value ? "1" : "0")},
                createdAt = '{creada}T12:00:00-05:00'
            WHERE id = '{cotizacion.Id}';
            """);
        return cotizacion;
    }

    /// <summary>Versión nueva (fila con el folio <c>-vN</c>) de <paramref name="original"/>, ganada y con otro precio.</summary>
    private Task NuevaVersionAsync(clsCotizacionPrueba original, int version, decimal precio, string creada) =>
        clsBaseDatosPruebas.EjecutarAsync(entorno.BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, usuarioId, costo, margenPolitica, margenAplicado, metodoCalculo,
                precioFinal, moneda, trmAplicada, estadoEnvio, ganada, version, createdAt, updatedAt)
            SELECT NEWID(), CONCAT(folio, N'-v{version}'), lineaNegocioId, usuarioId, costo, margenPolitica, margenAplicado, metodoCalculo,
                {clsApiWebPruebas.Numero(precio)}, moneda, trmAplicada, N'sin_enviar', 1, {version}, '{creada}T12:00:00-05:00', SYSDATETIMEOFFSET()
            FROM dbo_cotifly.Cotizaciones WHERE id = '{original.Id}';
            """);
}
