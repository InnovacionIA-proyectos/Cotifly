using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// #173: la propuesta <c>db/scripts/pendientes/05_categoria_conexo.sql</c> en una base aislada instalada desde cero (01 + 02): agrega la
/// columna de la categoría del conexo, NULL en las filas anteriores, y se puede repetir.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCategoriaConexoScriptTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task AgregaLaColumnaNullableYEsIdempotente()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbCategoriaConexo173");
        var script = clsBaseDatosPruebas.RutaScriptInstalacion(Path.Combine("pendientes", "05_categoria_conexo.sql"));

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, script);
        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, script);

        (await clsBaseDatosPruebas.EscalarAsync<string>(aislada.Cadena, """
            SELECT CONCAT(DATA_TYPE, '|', CHARACTER_MAXIMUM_LENGTH, '|', IS_NULLABLE) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_NAME = 'CotizacionServiciosConexos' AND COLUMN_NAME = 'categoriaServicioConexo'
            """)).Should().Be("nvarchar|20|YES");
    }
}
