using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// #173: <c>db/scripts/05_categoria_conexo.sql</c> (en <c>db/scripts/</c> desde HU-014, #188) en una base aislada instalada desde cero:
/// deja la columna de la categoría del conexo (NULL en las filas anteriores) y se puede repetir.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCategoriaConexoScriptTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task AgregaLaColumnaNullableYEsIdempotente()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbCategoriaConexo173");
        var script = clsBaseDatosPruebas.RutaScriptInstalacion("05_categoria_conexo.sql");

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, script);
        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, script);

        (await clsBaseDatosPruebas.EscalarAsync<string>(aislada.Cadena, """
            SELECT CONCAT(DATA_TYPE, '|', CHARACTER_MAXIMUM_LENGTH, '|', IS_NULLABLE) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_NAME = 'CotizacionServiciosConexos' AND COLUMN_NAME = 'categoriaServicioConexo'
            """)).Should().Be("nvarchar|20|YES");
    }

    /// <summary>
    /// #173/#191: el <c>06_idempotencia_captura.sql</c> (ya en <c>db/scripts/</c>) crea la tabla de claves con
    /// <c>UNIQUE (usuarioId, operacion, clave)</c> y se puede repetir.
    /// </summary>
    [Fact]
    public async Task El06CreaLaTablaDeClavesConSuUnicoYEsIdempotente()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbIdempotencia173");
        var script = clsBaseDatosPruebas.RutaScriptInstalacion("06_idempotencia_captura.sql");

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, script);
        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, script);

        var insertar = """
            INSERT INTO dbo_cotifly.CapturasIdempotentes (usuarioId, operacion, clave, huella, estado, expiraAt)
            VALUES ('11111111-1111-1111-1111-111111111111', N'capturar_costeo', N'clave-173-xx', REPLICATE('a', 64), N'libre', DATEADD(HOUR, 24, SYSDATETIMEOFFSET()));
            """;
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, insertar);
        var repetida = () => clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, insertar);
        await repetida.Should().ThrowAsync<Exception>("la misma clave del mismo usuario y operación choca con el UNIQUE");
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, insertar.Replace("capturar_costeo", "conexos", StringComparison.Ordinal));
    }
}
