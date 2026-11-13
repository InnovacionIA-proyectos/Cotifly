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
/// Pasos de <c>specs/features/margen_conexos_por_ruta.feature</c> (ADR-019 decisiones 3 y 6): margen y conexos POR
/// RUTA por HTTP. Reutiliza las cotizaciones del formulario de <see cref="clsPasosCosteo"/> y las licitaciones de
/// <see cref="clsPasosLicitaciones"/>. Las mutaciones envían <c>If-Match</c> con la versión vigente leída de la base.
/// </summary>
[Binding]
public sealed class clsPasosMargenConexosPorRuta(clsContextoWeb web, clsPasosCosteo costeo, clsPasosLicitaciones licitaciones)
{
    [When("{string} ajusta el margen de la licitación {string} a {string} sin indicar la ruta")]
    public async Task CuandoAjustaSinRuta(string alias, string licitacion, string margen) =>
        await web.EnviarAsync(HttpMethod.Put, $"{RutaCosteo(licitaciones.Folio(licitacion))}/margen", web.Sesion(alias).Access,
            $$"""{"margen":{{margen}}}""", await VersionVigenteAsync(licitaciones.Folio(licitacion)));

    [When("{string} ajusta el margen de la ruta {int} de la licitación {string} a {string}")]
    public async Task CuandoAjustaLaRuta(string alias, int orden, string licitacion, string margen) =>
        await web.EnviarAsync(HttpMethod.Put, $"{RutaCosteo(licitaciones.Folio(licitacion))}/margen", web.Sesion(alias).Access,
            $$"""{"margen":{{margen}},"linea_orden":{{orden}}}""", await VersionVigenteAsync(licitaciones.Folio(licitacion)));

    [Then("el detalle del error lista las {int} rutas de {string} sin montos")]
    public void EntoncesElDetalleListaLasRutas(int rutas, string licitacion)
    {
        var lista = web.UltimoJson().GetProperty("error").GetProperty("detalle").GetProperty("rutas").EnumerateArray().ToList();
        lista.Select(r => r.GetProperty("linea_orden").GetInt32()).Should().Equal(Enumerable.Range(1, rutas), licitacion);
        lista.Should().OnlyContain(r => r.EnumerateObject().All(p => p.Name == "linea_orden" || p.Name == "linea_solicitud_id" || p.Name == "origen" || p.Name == "destino"),
            "la lista de rutas no lleva montos");
    }

    [Then("ninguna ruta de {string} tiene margen negociado")]
    public async Task EntoncesSinNegociado(string licitacion) =>
        (await RutasAsync(licitaciones.Folio(licitacion))).Should().OnlyContain(r => r.GetProperty("origen_margen").GetString() != "negociado");

    [Then("no hay ajustes de margen de la licitación {string}")]
    public async Task EntoncesSinAjustes(string licitacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{licitaciones.Folio(licitacion)}'")).Should().Be(0);

    [Then("solo la ruta {int} de {string} tiene el margen {string} con el origen {string}")]
    public async Task EntoncesSoloEsaRuta(int orden, string licitacion, string margen, string origen)
    {
        var rutas = await RutasAsync(licitaciones.Folio(licitacion));
        var ajustada = rutas.Single(r => r.GetProperty("linea_orden").GetInt32() == orden);
        ajustada.GetProperty("margen_aplicado").GetDecimal().Should().Be(decimal.Parse(margen, CultureInfo.InvariantCulture));
        ajustada.GetProperty("origen_margen").GetString().Should().Be(origen);
        rutas.Where(r => r.GetProperty("linea_orden").GetInt32() != orden)
            .Should().OnlyContain(r => r.GetProperty("origen_margen").GetString() != "negociado", "las demás rutas no cambian");
    }

    [Then("el precio de la ruta {int} de {string} es {int} y el total es la suma de sus rutas")]
    public async Task EntoncesElPrecioDeLaRuta(int orden, string licitacion, int precio)
    {
        var rutas = await RutasAsync(licitaciones.Folio(licitacion));
        rutas.Single(r => r.GetProperty("linea_orden").GetInt32() == orden).GetProperty("precio_final_linea").GetDecimal().Should().Be(precio);
        web.UltimoJson().GetProperty("precio_final").GetDecimal().Should().Be(rutas.Sum(r => r.GetProperty("precio_final_linea").GetDecimal()));
    }

    [Then("el ajuste de margen de {string} quedó registrado en la ruta {int}")]
    public async Task EntoncesElAjusteEnLaRuta(string cotizacion, int orden)
    {
        var folio = FolioDe(cotizacion);
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes a "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = a.lineaSolicitudId "
            + $"WHERE a.folio = '{folio}' AND l.orden = {orden.ToString(CultureInfo.InvariantCulture)}"))
            .Should().Be(1, "MargenAjustes.lineaSolicitudId apunta a la ruta ajustada");
    }

    [When("{string} deja el servicio conexo {string} en la ruta {int} de {string} con la cantidad {int}")]
    public async Task CuandoCambiaLaCantidad(string alias, string nombre, int orden, string cotizacion, int cantidad)
    {
        var folio = costeo.Folio(cotizacion);
        var id = await IdConexoAsync(alias, folio, nombre);
        await web.EnviarAsync(HttpMethod.Put, $"{RutaCosteo(folio)}/servicios-conexos", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { linea_orden = orden, conexos = new[] { new { id, cantidad } } }), await VersionVigenteAsync(folio));
    }

    [When("{string} quita el servicio conexo {string} de la ruta {int} de {string}")]
    public async Task CuandoQuita(string alias, string nombre, int orden, string cotizacion)
    {
        var folio = costeo.Folio(cotizacion);
        var id = await IdConexoAsync(alias, folio, nombre);
        var conservar = (await RutasAsync(folio, alias)).Single(r => r.GetProperty("linea_orden").GetInt32() == orden)
            .GetProperty("servicios_conexos").EnumerateArray()
            .Where(s => s.GetProperty("id").GetString() != id)
            .Select(s => new { id = s.GetProperty("id").GetString(), cantidad = s.GetProperty("cantidad").GetDecimal() })
            .ToList();
        await web.EnviarAsync(HttpMethod.Put, $"{RutaCosteo(folio)}/servicios-conexos", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { linea_orden = orden, conexos = conservar }), await VersionVigenteAsync(folio));
    }

    [Then("la ruta {int} de {string} no tiene servicios conexos guardados")]
    public async Task EntoncesSinConexosEnLaBase(int orden, string cotizacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos s "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
            + "JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden.ToString(CultureInfo.InvariantCulture)}"))
            .Should().Be(0, "quitar un conexo borra su fila");

    [Then("quedó auditado que se quitó el servicio conexo {string} de {string}")]
    public async Task EntoncesAuditado(string nombre, string cotizacion)
    {
        // Determinista: el cambio de cantidad y el quitar pueden quedar con el mismo createdAt (y el id es NEWID, no sirve de
        // desempate), así que se busca el evento del quitar en vez de "el último".
        var salida = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT TOP 1 outputResumen FROM dbo_cotifly.EventosHerramienta "
            + $"WHERE herramienta = 'cambiar_servicios_conexos' AND exito = 1 AND input LIKE '%{costeo.Folio(cotizacion)}%' "
            + "AND outputResumen LIKE '%\"quitado\":true%' ORDER BY createdAt DESC");
        salida.Should().Contain(nombre).And.Contain("\"quitado\":true");
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

    private async Task<string> IdConexoAsync(string alias, string folio, string nombre) =>
        (await RutasAsync(folio, alias)).SelectMany(r => r.GetProperty("servicios_conexos").EnumerateArray())
            .Single(s => s.GetProperty("nombre").GetString() == nombre).GetProperty("id").GetString()!;

    /// <summary>Las rutas del costeo (GET con la sesión de <paramref name="alias"/>; la respuesta queda en el contexto).</summary>
    private async Task<IReadOnlyList<JsonElement>> RutasAsync(string folio, string alias = "ana")
    {
        await web.EnviarAsync(HttpMethod.Get, RutaCosteo(folio), web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        return [.. web.UltimoJson().GetProperty("rutas").EnumerateArray()];
    }

    private static async Task<string> VersionVigenteAsync(string folio)
    {
        var version = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");
        return $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private static string RutaCosteo(string folio) => $"/web/cotizaciones/{folio}/costeo";
}
