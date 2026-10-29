using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Contrato;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/admin_lineas_negocio.feature</c> (HU-004.9). Los números van sin comillas en el <c>.feature</c> y se
/// leen con cultura invariante (la del <c>.feature</c> es español, donde el punto es separador de miles).
/// </summary>
[Binding]
public sealed class clsPasosAdmin(clsContextoWeb web)
{
    private const string Numero = "(-?[0-9]+(?:\\.[0-9]+)?)";

    [Given(@"^la línea de negocio de prueba ""([^""]+)"" con margen " + Numero + @", método ""([^""]+)"" y activa$")]
    public async Task DadoLaLineaDePrueba(string alias, string margen, string metodo)
    {
        var nombre = $"{alias}{Guid.NewGuid():N}"[..Math.Min(alias.Length + 12, 40)];
        var id = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive) VALUES (N'{nombre}', {margen}, N'{metodo}', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);
        web.Lineas[alias] = (id, nombre);
    }

    [When("{string} lista las líneas de negocio")]
    public Task CuandoLista(string alias) => web.EnviarAsync(HttpMethod.Get, "/web/admin/lineas-negocio", web.Sesion(alias).Access);

    [When(@"^""([^""]+)"" actualiza la línea ""([^""]+)"" con margen " + Numero + @", método ""([^""]+)"" y activa ""(true|false)""$")]
    public Task CuandoActualiza(string alias, string linea, string margen, string metodo, string activa) =>
        Actualizar(alias, web.Lineas[linea].Id, $$"""{"margen": {{margen}}, "metodo_calculo": "{{metodo}}", "is_active": {{activa}}}""");

    [When(@"^""([^""]+)"" actualiza la línea con id ([0-9]+) con margen " + Numero + @", método ""([^""]+)"" y activa ""(true|false)""$")]
    public Task CuandoActualizaPorId(string alias, int id, string margen, string metodo, string activa) =>
        Actualizar(alias, id, $$"""{"margen": {{margen}}, "metodo_calculo": "{{metodo}}", "is_active": {{activa}}}""");

    [When(@"^""([^""]+)"" actualiza la línea ""([^""]+)"" con el cuerpo (.+)$")]
    public Task CuandoActualizaConCuerpo(string alias, string linea, string cuerpo) => Actualizar(alias, web.Lineas[linea].Id, cuerpo);

    [Then("el listado de líneas incluye las 8 líneas sembradas y {string}")]
    public void EntoncesElListadoIncluye(string alias) =>
        web.UltimoJson().EnumerateArray().Select(l => l.GetProperty("linea_negocio").GetString()).Should().Contain(
            ["Nacional", "Distribucion", "Importacion", "Exportacion", "Internacional", "DevolucionContenedor", web.Lineas[alias].Nombre]); // HU-021 (0009)

    [Then("cada línea trae id, linea_negocio, margen, metodo_calculo e is_active")]
    public async Task EntoncesCadaLineaCumpleElContrato()
    {
        var esquema = await clsValidadorEsquemaOpenApi.EsquemaRespuestaAsync("/web/admin/lineas-negocio", HttpMethod.Get, System.Net.HttpStatusCode.OK);
        clsValidadorEsquemaOpenApi.Validar(web.UltimoCuerpo, esquema).Should().BeEmpty();
    }

    [Then(@"^la línea ""([^""]+)"" queda con margen " + Numero + @", método ""([^""]+)"" (?:e|y) (activa|inactiva)$")]
    public async Task EntoncesLaLineaQueda(string alias, string margen, string metodo, string estado)
    {
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(CAST(margen AS decimal(9,4)), '|', metodoCalculo, '|', CAST(isActive AS nvarchar(1))) FROM dbo_cotifly.MargenesPolitica WHERE id = {web.Lineas[alias].Id}");
        var partes = fila!.Split('|');

        decimal.Parse(partes[0], CultureInfo.InvariantCulture).Should().Be(decimal.Parse(margen, CultureInfo.InvariantCulture));
        partes[1].Should().Be(metodo);
        partes[2].Should().Be(estado == "activa" ? "1" : "0");
    }

    [Then(@"^la auditoría registra que ""([^""]+)"" cambió ""([^""]+)"" de margen " + Numero + " a " + Numero + "$")]
    public async Task EntoncesLaAuditoria(string alias, string linea, string antes, string despues)
    {
        var evento = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT TOP 1 CONCAT(input, '|', outputResumen) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'admin_actualizar_linea_negocio' AND input LIKE '%\"linea_negocio_id\":{web.Lineas[linea].Id},%' ORDER BY createdAt DESC");

        evento.Should().NotBeNull();
        evento.Should().Contain(web.Sesion(alias).UsuarioId.ToString()).And.Contain(web.Sesion(alias).NombreUsuario);
        var resultado = evento![(evento.IndexOf('|', StringComparison.Ordinal) + 1)..];
        resultado.Should().Contain("\"antes\":{").And.Contain("\"despues\":{");
        MargenDe(resultado, "antes").Should().Be(decimal.Parse(antes, CultureInfo.InvariantCulture));
        MargenDe(resultado, "despues").Should().Be(decimal.Parse(despues, CultureInfo.InvariantCulture));
    }

    /// <summary>HU-016.1 (#154): una línea de prueba inactiva (no debe salir en el desplegable).</summary>
    [Given(@"^la línea de negocio de prueba ""([^""]+)"" con margen " + Numero + @", método ""([^""]+)"" e inactiva$")]
    public async Task DadoLaLineaDePruebaInactiva(string alias, string margen, string metodo)
    {
        var nombre = $"{alias}{Guid.NewGuid():N}"[..Math.Min(alias.Length + 12, 40)];
        var id = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive) VALUES (N'{nombre}', {margen}, N'{metodo}', 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);
        web.Lineas[alias] = (id, nombre);
    }

    [When("{string} lista las líneas de negocio activas")]
    public Task CuandoListaLasActivas(string alias) => web.EnviarAsync(HttpMethod.Get, "/web/lineas-negocio", web.Sesion(alias).Access);

    [Then("el listado de líneas activas cumple el esquema {string} del contrato")]
    public async Task EntoncesElListadoActivoCumpleElEsquema(string esquema)
    {
        var deContrato = await clsValidadorEsquemaOpenApi.EsquemaRespuestaAsync("/web/lineas-negocio", HttpMethod.Get, System.Net.HttpStatusCode.OK);
        clsValidadorEsquemaOpenApi.Validar(web.UltimoCuerpo, deContrato).Should().BeEmpty();
        var elemento = await clsValidadorEsquemaOpenApi.EsquemaAsync(esquema);
        foreach (var linea in web.UltimoJson().EnumerateArray())
        {
            clsValidadorEsquemaOpenApi.Validar(linea.GetRawText(), elemento).Should().BeEmpty();
        }
    }

    [Then("el listado de líneas activas incluye las 6 líneas sembradas y {string}")]
    public void EntoncesElListadoActivoIncluye(string alias) =>
        NombresActivos().Should().Contain(
            ["Nacional", "Distribucion", "Importacion", "Exportacion", "Internacional", "DevolucionContenedor", web.Lineas[alias].Nombre]);

    [Then("el listado de líneas activas no incluye {string}")]
    public void EntoncesElListadoActivoNoIncluye(string alias) => NombresActivos().Should().NotContain(web.Lineas[alias].Nombre);

    [Then("ninguna línea del listado activo trae margen ni método de cálculo")]
    public void EntoncesSinMargen() =>
        web.UltimoJson().EnumerateArray().SelectMany(l => l.EnumerateObject().Select(p => p.Name)).Distinct()
            .Should().BeEquivalentTo(["linea_negocio", "etiqueta"], "la política de margen no sale de la administración");

    [Then("la línea {string} del listado activo tiene la etiqueta {string}")]
    public void EntoncesLaEtiqueta(string linea, string etiqueta) =>
        web.UltimoJson().EnumerateArray().Single(l => l.GetProperty("linea_negocio").GetString() == linea)
            .GetProperty("etiqueta").GetString().Should().Be(etiqueta);

    private List<string?> NombresActivos()
    {
        web.UltimoEstado.Should().Be(System.Net.HttpStatusCode.OK, web.UltimoCuerpo);
        return [.. web.UltimoJson().EnumerateArray().Select(l => l.GetProperty("linea_negocio").GetString())];
    }

    private Task Actualizar(string alias, int id, string cuerpo) =>
        web.EnviarAsync(HttpMethod.Put, $"/web/admin/lineas-negocio/{id}", web.Sesion(alias).Access, cuerpo);

    private static decimal MargenDe(string resultado, string bloque)
    {
        using var json = System.Text.Json.JsonDocument.Parse(resultado);
        return json.RootElement.GetProperty(bloque).GetProperty("margen").GetDecimal();
    }
}
