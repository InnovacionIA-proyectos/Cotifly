using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>aplicar-mapeo</c> de <c>specs/features/licitaciones_excel.feature</c>. Usa el Nexo falso del escenario, que
/// aplica el mapeo con un subconjunto de las reglas de NEXO: toneladas, «ORIGEN - DESTINO», coma decimal y la línea de negocio por celda.
/// </summary>
[Binding]
public sealed class clsPasosAplicacionMapeo(clsContextoWeb web)
{
    /// <summary>
    /// <paramref name="mapeo"/>: <c>campo[:transformacion]</c> separados por comas, en el orden de las columnas; <paramref name="filas"/>:
    /// filas separadas por <c>|</c> y celdas por <c>;</c> (así la coma decimal queda dentro de la celda).
    /// </summary>
    [When("{string} aplica el mapeo {string} del cliente {string} desde la fila {int} a las filas {string}")]
    public Task CuandoAplicaElMapeo(string alias, string mapeo, string nit, int primeraFila, string filas)
    {
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            columnas = mapeo.Split(',').Select(c => c.Trim().Split(':')).Select((c, i) => new { indice = i, campo = c[0], transformacion = c.Length > 1 ? c[1] : null }),
            filas = filas.Split('|').Select(f => f.Split(';').Select(c => c.Trim()).ToArray()),
            primera_fila = primeraFila,
        });
        return web.EnviarAsync(HttpMethod.Post, "/web/licitaciones/aplicar-mapeo", web.Sesion(alias).Access, cuerpo);
    }

    [Then("la línea de la fila {int} va de {string} a {string} con {int} kg y la línea de negocio {string}")]
    public void EntoncesLaLinea(int fila, string origen, string destino, int pesoKg, string lineaNegocio)
    {
        var linea = Linea(fila);
        (linea.GetProperty("origen").GetString(), linea.GetProperty("destino").GetString(), linea.GetProperty("peso_kg").GetDecimal(),
            linea.GetProperty("linea_negocio").GetString()).Should().Be((origen, destino, (decimal)pesoKg, lineaNegocio));
    }

    [Then("la línea de la fila {int} va de {string} a {string} con {int} kg sin línea de negocio y con la línea de NEXO {string}")]
    public void EntoncesLaLineaSinEquivalencia(int fila, string origen, string destino, int pesoKg, string lineaNexo)
    {
        var linea = Linea(fila);
        (linea.GetProperty("origen").GetString(), linea.GetProperty("destino").GetString(), linea.GetProperty("peso_kg").GetDecimal())
            .Should().Be((origen, destino, (decimal)pesoKg));
        linea.GetProperty("linea_negocio").ValueKind.Should().Be(JsonValueKind.Null);
        linea.GetProperty("linea_nexo").GetString().Should().Be(lineaNexo);
    }

    [Then("la fila {int} del Excel trae el error {string} en el campo {string}")]
    public void EntoncesElError(int filaExcel, string codigo, string campo)
    {
        var error = web.UltimoJson().GetProperty("errores").EnumerateArray().Single(e => e.GetProperty("fila_excel").GetInt32() == filaExcel);
        (error.GetProperty("codigo").GetString(), error.GetProperty("campo").GetString()).Should().Be((codigo, campo));
    }

    private JsonElement Linea(int fila) =>
        web.UltimoJson().GetProperty("lineas").EnumerateArray().Single(l => l.GetProperty("fila").GetInt32() == fila);
}
