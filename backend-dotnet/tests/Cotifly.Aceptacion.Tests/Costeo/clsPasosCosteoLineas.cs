using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/margen_conexos_lineas.feature</c> (HU-041.1, #231, ADR-025): margen y servicios conexos para varias rutas en
/// una operación, por HTTP. Reutiliza las cotizaciones del formulario de <see cref="clsPasosCosteo"/> y las licitaciones de
/// <see cref="clsPasosLicitaciones"/>. Las mutaciones envían <c>If-Match</c> con la versión vigente leída de la base.
/// </summary>
[Binding]
public sealed class clsPasosCosteoLineas(clsContextoWeb web, clsPasosCosteo costeo, clsPasosLicitaciones licitaciones)
{
    [When("{string} aplica el margen {string} a todas las rutas de la licitación {string}")]
    public async Task CuandoAplicaElMargenATodas(string alias, string margen, string licitacion) =>
        await EnviarAsync(alias, licitaciones.Folio(licitacion), HttpMethod.Put, "margen/lineas", $$"""{"todas":true,"margen":{{margen}}}""");

    [When("{string} vuelve al margen sugerido todas las rutas de la licitación {string}")]
    public async Task CuandoVuelveAlSugerido(string alias, string licitacion) =>
        await EnviarAsync(alias, licitaciones.Folio(licitacion), HttpMethod.Put, "margen/lineas", """{"todas":true,"revertir_sugerido":true}""");

    [When("{string} agrega el servicio conexo {string} con la cantidad {int} a todas las rutas de {string}")]
    public async Task CuandoAgregaATodas(string alias, string servicio, int cantidad, string cotizacion) =>
        await EnviarAsync(alias, costeo.Folio(cotizacion), HttpMethod.Post, "servicios-conexos/lineas",
            clsApiWebPruebas.Json(new { todas = true, servicio_id = servicio, cantidad }));

    [Then("cada ruta de {string} tiene el margen {string} con el origen {string}")]
    public void EntoncesCadaRutaTieneElMargen(string cotizacion, string margen, string origen)
    {
        var rutas = web.UltimoJson().GetProperty("rutas").EnumerateArray().ToList();
        rutas.Should().NotBeEmpty(cotizacion);
        rutas.Should().OnlyContain(r => r.GetProperty("margen_aplicado").GetDecimal() == decimal.Parse(margen, CultureInfo.InvariantCulture)
                                        && r.GetProperty("origen_margen").GetString() == origen);
    }

    [Then("el error señala la ruta {int}")]
    public void EntoncesElErrorSenalaLaRuta(int orden)
    {
        var detalle = web.UltimoJson().GetProperty("error").GetProperty("detalle");
        detalle.GetProperty("linea_orden").GetInt32().Should().Be(orden);
        detalle.GetProperty("linea_solicitud_id").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Then("cada ruta de {string} tiene el conexo {string} con el servicio_id {string} pendiente de enviar a Nexo")]
    public void EntoncesCadaRutaTieneElConexo(string cotizacion, string nombre, string servicio)
    {
        var rutas = web.UltimoJson().GetProperty("rutas").EnumerateArray().ToList();
        rutas.Should().NotBeEmpty(cotizacion);
        foreach (var ruta in rutas)
        {
            var conexo = ruta.GetProperty("servicios_conexos").EnumerateArray().Single(c => c.GetProperty("nombre").GetString() == nombre);
            conexo.GetProperty("servicio_id").GetString().Should().Be(servicio);
            conexo.GetProperty("estado_nexo").GetString().Should().Be("pendiente", "el envío diferido lo lleva a Nexo en el siguiente ciclo");
        }
    }

    [Then("quedó una sola auditoría {string} de {string}")]
    public async Task EntoncesUnaSolaAuditoria(string herramienta, string cotizacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{herramienta}' AND input LIKE '%{FolioDe(cotizacion)}%'"))
        .Should().Be(1);

    private async Task EnviarAsync(string alias, string folio, HttpMethod metodo, string ruta, string cuerpo)
    {
        await web.EnviarAsync(metodo, $"/web/cotizaciones/{folio}/costeo/{ruta}", web.Sesion(alias).Access, cuerpo, await VersionVigenteAsync(folio));
        web.UltimoEstado.Should().NotBe(HttpStatusCode.PreconditionRequired, web.UltimoCuerpo);
    }

    private string FolioDe(string cotizacion)
    {
        try
        {
            return costeo.Folio(cotizacion);
        }
        catch (InvalidOperationException)
        {
            return licitaciones.Folio(cotizacion);
        }
    }

    private static async Task<string> VersionVigenteAsync(string folio)
    {
        var version = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");
        return $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }
}
