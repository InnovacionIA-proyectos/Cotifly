using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/metricas_tablero.feature</c> (HU-010.1, ADR-016): cotizaciones con valores del tablero fijados por
/// SQL, versiones nuevas, consultas a <c>/web/metricas/comercial</c> y <c>/regional</c> y comprobación de los indicadores.
/// Reutiliza la sesión, las regionales y la última respuesta de <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosMetricas(clsContextoWeb web)
{
    private static readonly HttpMethod _get = HttpMethod.Get;

    private static string CadenaCotifly => clsEntornoAceptacion.BaseDatos.CadenaCotifly;

    [Given("el gerente comercial {string} con sesión y sin regional")]
    public async Task DadoElGerenteSinRegional(string alias) =>
        web.Sesiones[alias] = await web.Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, regional: null, $"Persona {alias}");

    [Given("que {string} tiene estas cotizaciones:")]
    public async Task DadoLasCotizaciones(string alias, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var usuarioId = web.Sesion(alias).UsuarioId;
        foreach (var fila in tabla.Rows)
        {
            var cotizacion = await web.Api.CrearCotizacionAsync(usuarioId, conPdf: false);
            web.Cotizaciones[fila["cotizacion"]] = cotizacion;
            await clsBaseDatosPruebas.EjecutarAsync(CadenaCotifly, $"""
                UPDATE dbo_cotifly.Cotizaciones
                SET moneda = N'{fila["moneda"]}', trmAplicada = {Numero(fila["trm"]) ?? "0"}, precioFinal = {Numero(fila["precio"]) ?? "NULL"},
                    costo = {Numero(fila["costo"]) ?? "NULL"}, margenAplicado = {Numero(fila["margen"]) ?? "NULL"},
                    ganada = {Resultado(fila["resultado"])}, createdAt = '{fila["creada"]}T12:00:00-05:00'
                WHERE id = '{cotizacion.Id}';
                """);
        }
    }

    [Given(@"^que ""([^""]+)"" rehízo la cotización ""([^""]+)"" como versión (\d+) con precio (\d+) y costo (\d+)$")]
    public Task DadoLaNuevaVersion(string alias, string cotizacion, int version, string precio, string costo)
    {
        web.Sesion(alias).Should().NotBeNull();
        var original = web.Cotizacion(cotizacion);
        return clsBaseDatosPruebas.EjecutarAsync(CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, usuarioId, costo, margenPolitica, margenAplicado, metodoCalculo,
                precioFinal, moneda, trmAplicada, estadoEnvio, ganada, version, createdAt, updatedAt)
            SELECT NEWID(), CONCAT(folio, N'-v{version}'), lineaNegocioId, usuarioId, {costo}, margenPolitica, margenAplicado, metodoCalculo,
                {precio}, moneda, trmAplicada, N'sin_enviar', ganada, {version}, DATEADD(DAY, 1, createdAt), SYSDATETIMEOFFSET()
            FROM dbo_cotifly.Cotizaciones WHERE id = '{original.Id}';
            """);
    }

    [When("{string} consulta sus métricas del {string} al {string}")]
    public Task CuandoConsultaSusMetricas(string alias, string desde, string hasta) =>
        web.EnviarAsync(_get, $"/web/metricas/comercial?desde={desde}&hasta={hasta}", web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional del {string} al {string}")]
    public Task CuandoConsultaLaRegional(string alias, string desde, string hasta) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?desde={desde}&hasta={hasta}", web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional del {string} al {string} filtradas por {string}")]
    public Task CuandoConsultaLaRegionalFiltrada(string alias, string desde, string hasta, string comercial) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?desde={desde}&hasta={hasta}&usuario_id={web.Sesion(comercial).UsuarioId}",
            web.Sesion(alias).Access);

    [Then("los indicadores son:")]
    public void EntoncesLosIndicadores(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var indicadores = web.UltimoJson().GetProperty("indicadores");
        foreach (var fila in tabla.Rows)
        {
            var valor = indicadores.GetProperty(fila["campo"]);
            if (fila["valor"] == "null")
            {
                valor.ValueKind.Should().Be(JsonValueKind.Null, fila["campo"]);
            }
            else
            {
                valor.GetDecimal().Should().Be(decimal.Parse(fila["valor"], CultureInfo.InvariantCulture), fila["campo"]);
            }
        }
    }

    [Then("la serie mensual tiene el mes {string} con {int} cotizaciones y {int} ganadas")]
    public void EntoncesLaSerie(string mes, int cantidad, int ganadas)
    {
        var punto = web.UltimoJson().GetProperty("serie_mensual").EnumerateArray().Single(p => p.GetProperty("mes").GetString() == mes);
        punto.GetProperty("cantidad").GetInt32().Should().Be(cantidad);
        punto.GetProperty("ganadas").GetInt32().Should().Be(ganadas);
    }

    [Then("la regional de las métricas es {string}")]
    public void EntoncesLaRegional(string alias) => web.UltimoJson().GetProperty("regional").GetString().Should().Be(web.Regional(alias));

    [Then("los comerciales de las métricas son {string}, {string} y {string}")]
    public void EntoncesLosComerciales(string uno, string dos, string tres) =>
        web.UltimoJson().GetProperty("comerciales").EnumerateArray().Select(c => c.GetProperty("usuario_id").GetString())
            .Should().BeEquivalentTo([.. new[] { uno, dos, tres }.Select(a => web.Sesion(a).UsuarioId.ToString())]);

    private static string? Numero(string texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : clsApiWebPruebas.Numero(decimal.Parse(texto, CultureInfo.InvariantCulture));

    private static string Resultado(string texto) => texto switch
    {
        "ganada" => "1",
        "perdida" => "0",
        _ => "NULL",
    };
}
