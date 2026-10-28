using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/licitacion_solo_lectura.feature</c> (HU-034.1, #201, parte A): la licitación y su Excel para el gerente de
/// la regional del dueño y el director, en solo lectura, sin costos y auditados. Reutiliza las licitaciones y los Excel bajados de
/// <see cref="clsPasosLicitaciones"/> y las sesiones de <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosLicitacionSoloLectura(clsContextoWeb web, clsPasosLicitaciones licitaciones)
{
    [When("{string} consulta la licitación {string}")]
    public Task CuandoConsulta(string alias, string licitacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{licitaciones.Folio(licitacion)}", web.Sesion(alias).Access);

    [Then(@"^la licitación consultada es ""([^""]+)"" y (no es|es) de solo consulta$")]
    public void EntoncesLaLicitacion(string licitacion, string soloConsulta)
    {
        var json = web.UltimoJson();
        json.GetProperty("folio").GetString().Should().Be(licitaciones.Folio(licitacion));
        json.GetProperty("solo_lectura").GetBoolean().Should().Be(soloConsulta == "es");
    }

    [Then("la licitación consultada no contiene el costo {int} de Nexo")]
    public void EntoncesSinCosto(int costo) =>
        web.UltimoCuerpo.Should().NotContain(costo.ToString(System.Globalization.CultureInfo.InvariantCulture)).And.NotContain("\"costo");

    [Then(@"^la consulta de ""([^""]+)"" a la licitación ""([^""]+)"" queda en la auditoría como (permitida|negada)$")]
    public Task EntoncesConsultaAuditada(string alias, string licitacion, string resultado) =>
        AuditadaAsync("consultar_licitacion", alias, licitacion, resultado);

    [Then(@"^la descarga del Excel de ""([^""]+)"" de la licitación ""([^""]+)"" queda en la auditoría como (permitida|negada)$")]
    public Task EntoncesDescargaAuditada(string alias, string licitacion, string resultado) =>
        AuditadaAsync("exportar_excel_licitacion", alias, licitacion, resultado);

    [Then("el Excel es igual al que bajó {string}")]
    public void EntoncesElMismoExcel(string alias) => Celdas(web.UltimoContenido).Should().Equal(Celdas(licitaciones.Excels[alias]));

    [When("{string} intenta exportar el Excel de la licitación {string}")]
    public Task CuandoIntentaExportar(string alias, string licitacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{licitaciones.Folio(licitacion)}/excel", web.Sesion(alias).Access);

    [When(@"^""([^""]+)"" intenta reintentar la cubicación de la licitación ""([^""]+)"" (con|sin) el respaldo$")]
    public Task CuandoIntentaReintentarCubicacion(string alias, string licitacion, string respaldo) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/licitaciones/{licitaciones.Folio(licitacion)}/cubicacion/reintentar", web.Sesion(alias).Access,
            respaldo == "con" ? """{"usar_respaldo":true}""" : """{"usar_respaldo":false}""");

    [When("{string} intenta reintentar el envío de la licitación {string}")]
    public Task CuandoIntentaReintentarEnvio(string alias, string licitacion) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{licitaciones.Folio(licitacion)}/costeo/reintentar", web.Sesion(alias).Access);

    private async Task AuditadaAsync(string herramienta, string alias, string licitacion, string resultado) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{herramienta}' AND input LIKE '%{licitaciones.Folio(licitacion)}%' " +
            $"AND input LIKE '%{web.Sesion(alias).UsuarioId}%' AND exito = {(resultado == "permitida" ? 1 : 0)}"))
            .Should().Be(1);

    /// <summary>Todas las celdas usadas del libro (hoja!celda=texto), sin la fila "Generado" del resumen (la hora de la descarga).</summary>
    private static List<string> Celdas(byte[] excel)
    {
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(excel));
        return [.. libro.Worksheets.SelectMany(h => h.CellsUsed()
            .Where(c => !string.Equals(c.WorksheetRow().Cell(1).GetString(), "Generado", StringComparison.Ordinal))
            .Select(c => $"{h.Name}!{c.Address}={c.GetFormattedString()}"))];
    }
}
