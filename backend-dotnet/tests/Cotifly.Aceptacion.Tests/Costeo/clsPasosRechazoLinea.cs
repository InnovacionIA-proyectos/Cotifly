using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// HU-028.1 (#183, Nexo #320), pasos de <c>specs/features/rechazo_linea_nexo.feature</c>: la Mesa de Costeo del Nexo falso rechaza (o deja de
/// rechazar) una línea y Cotifly lo refleja en la licitación, en la base (el camión con su motivo, script 10), en el Excel, en el PDF y en el
/// margen. Reutiliza los pasos de la licitación (<see cref="clsPasosLicitaciones"/>) y el Nexo falso de <see cref="clsPasosCosteo"/>.
/// </summary>
[Binding]
public sealed class clsPasosRechazoLinea(clsContextoWeb web, clsPasosCosteo costeo, clsPasosLicitaciones licitaciones)
{
    [When("la Mesa de Costeo de Nexo rechaza la línea {int} de {string} con el motivo {string}")]
    public async Task CuandoRechaza(int linea, string licitacion, string motivo) =>
        costeo.Nexo.RechazarLinea(await CosteoIdAsync(licitacion), linea - 1, motivo);

    [When("la Mesa de Costeo de Nexo rechaza las {int} líneas de {string} con el motivo {string}")]
    public async Task CuandoRechazaTodas(int lineas, string licitacion, string motivo)
    {
        var id = await CosteoIdAsync(licitacion);
        for (var i = 0; i < lineas; i++)
        {
            costeo.Nexo.RechazarLinea(id, i, motivo);
        }
    }

    [When("la Mesa de Costeo de Nexo deshace el rechazo de la línea {int} de {string}")]
    public async Task CuandoDeshace(int linea, string licitacion) =>
        costeo.Nexo.DeshacerRechazoLinea(await CosteoIdAsync(licitacion), linea - 1);

    [When("Nexo entrega la cotización de la licitación {string}")]
    public async Task CuandoEntrega(string licitacion) => costeo.Nexo.Entregar(await CosteoIdAsync(licitacion));

    [When("{string} intenta generar el PDF de la licitación {string}")]
    public Task CuandoIntentaPdf(string alias, string licitacion) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{licitaciones.Folio(licitacion)}/costeo/pdf", web.Sesion(alias).Access);

    [Then("la línea {int} de {string} está rechazada con el motivo {string} y sin precio")]
    public async Task EntoncesLineaRechazada(int linea, string licitacion, string motivo)
    {
        var vista = await VistaAsync(licitacion);
        var fila = vista.GetProperty("lineas").EnumerateArray().Single(l => l.GetProperty("orden").GetInt32() == linea);
        fila.GetProperty("estado_costeo").GetString().Should().Be("rechazada");
        fila.GetProperty("motivo_rechazo").GetString().Should().Be(motivo);
        fila.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        vista.GetProperty("lineas_rechazadas").GetInt32().Should().Be(1);
        vista.GetProperty("lineas_pendientes").GetInt32().Should().Be(0, "una línea rechazada no es pendiente");

        // Lo mismo en GET .../costeo (RutaCosteo y CamionCosteo).
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{licitaciones.Folio(licitacion)}/costeo", web.Sesion("ana").Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var ruta = web.UltimoJson().GetProperty("rutas").EnumerateArray().Single(r => r.GetProperty("linea_orden").GetInt32() == linea);
        ruta.GetProperty("estado_costeo").GetString().Should().Be("rechazada");
        ruta.GetProperty("motivo_rechazo").GetString().Should().Be(motivo);
        ruta.GetProperty("precio_final_linea").ValueKind.Should().Be(JsonValueKind.Null);
        var camion = ruta.GetProperty("camiones").EnumerateArray().Should().ContainSingle().Subject;
        camion.GetProperty("estado_costeo").GetString().Should().Be("rechazada");
        camion.GetProperty("motivo_rechazo").GetString().Should().Be(motivo);
        camion.GetProperty("rechazada_at").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Then("el precio final de {string} es la suma de sus líneas con precio")]
    public async Task EntoncesSumaConPrecio(string licitacion)
    {
        var vista = await VistaAsync(licitacion);
        var suma = vista.GetProperty("lineas").EnumerateArray()
            .Where(l => l.GetProperty("precio_final").ValueKind != JsonValueKind.Null)
            .Sum(l => l.GetProperty("precio_final").GetDecimal());
        suma.Should().BePositive();
        vista.GetProperty("precio_final").GetDecimal().Should().Be(suma);
    }

    [Then("el camión de la ruta {int} de {string} quedó guardado como rechazado con el motivo {string}")]
    public async Task EntoncesGuardadoRechazado(int ruta, string licitacion, string motivo) =>
        (await CamionAsync(ruta, licitacion)).Should().Be($"rechazada|{motivo}|con fecha|sin flete");

    [Then("el camión de la ruta {int} de {string} quedó guardado como {string} sin motivo de rechazo")]
    public async Task EntoncesGuardadoSinRechazo(int ruta, string licitacion, string estado) =>
        (await CamionAsync(ruta, licitacion)).Should().Be($"{estado}||sin fecha|sin flete");

    [Then("el Excel no contiene el texto {string}")]
    public void EntoncesExcelSinTexto(string texto)
    {
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(web.UltimoContenido));
        libro.Worksheets.SelectMany(h => h.CellsUsed()).Select(c => c.GetFormattedString()).Should().NotContain(c => c.Contains(texto));
    }

    [Then("el PDF del pliego no contiene el texto {string}")]
    public void EntoncesPdfSinTexto(string texto) => clsLectorPdf.Leer(web.UltimoContenido).Texto.Should().NotContain(texto);

    [Then("la licitación {string} no tiene precio final y avisa que la Mesa rechazó todas las rutas")]
    public async Task EntoncesSinPrecio(string licitacion)
    {
        var vista = await VistaAsync(licitacion);
        vista.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        vista.GetProperty("lineas_rechazadas").GetInt32().Should().Be(vista.GetProperty("lineas").GetArrayLength());
        vista.GetProperty("motivo_estado_costeo").GetString().Should().Contain("rechazó todas las rutas");
    }

    private async Task<JsonElement> VistaAsync(string licitacion)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{licitaciones.Folio(licitacion)}", web.Sesion("ana").Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        return web.UltimoJson();
    }

    private async Task<string?> CamionAsync(int ruta, string licitacion) =>
        await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT CONCAT(v.estadoCosteo, '|', v.motivoRechazo, '|', CASE WHEN v.rechazadaAt IS NULL THEN 'sin fecha' ELSE 'con fecha' END, '|', "
            + "CASE WHEN v.costoFlete IS NULL THEN 'sin flete' ELSE 'con flete' END) "
            + "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{licitaciones.Folio(licitacion)}' AND l.orden = {ruta}");

    private async Task<string> CosteoIdAsync(string licitacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT solicitudNexoId FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{licitaciones.Folio(licitacion)}'"))!;
}
