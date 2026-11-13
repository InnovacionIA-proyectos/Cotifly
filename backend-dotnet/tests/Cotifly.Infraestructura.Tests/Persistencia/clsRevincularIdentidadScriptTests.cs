using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// <c>db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql</c> en una base aislada instalada desde cero. Respalda
/// los vínculos <c>UsuariosWeb.idUsuarioExterno</c>, los pone en NULL y, si se repite, no toca los vínculos nuevos.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsRevincularIdentidadScriptTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static string Script => clsBaseDatosPruebas.RutaScriptInstalacion(Path.Combine("operacion", "revincular_identidad_dbSolucionesTI_Prueba.sql"));

    [Fact]
    public async Task RespaldaLosVinculosLosSueltaYAlRepetirloNoTocaLosVinculosNuevos()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbRevincular172");
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, """
            INSERT INTO dbo_cotifly.UsuariosWeb (id, nombreUsuario, idUsuarioExterno) VALUES
                ('11111111-1111-1111-1111-111111111111', N'dguerra', 430),
                ('22222222-2222-2222-2222-222222222222', N'otro', 431),
                ('33333333-3333-3333-3333-333333333333', N'sinvinculo', NULL);
            """);

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, Script);

        (await Escalar(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.UsuariosWeb WHERE idUsuarioExterno IS NOT NULL")).Should().Be(0);
        (await Escalar(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.RespaldoVinculosIdentidad_172")).Should().Be(2);
        (await Escalar(aislada.Cadena,
            "SELECT idUsuarioExterno FROM dbo_cotifly.RespaldoVinculosIdentidad_172 WHERE usuarioId = '11111111-1111-1111-1111-111111111111' AND respaldadoAt IS NOT NULL"))
            .Should().Be(430);

        // dguerra entra con la base nueva y queda vinculado al ID del portal; repetir el script no lo suelta.
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena,
            "UPDATE dbo_cotifly.UsuariosWeb SET idUsuarioExterno = 10123 WHERE id = '11111111-1111-1111-1111-111111111111';");
        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, Script);

        (await Escalar(aislada.Cadena, "SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE id = '11111111-1111-1111-1111-111111111111'"))
            .Should().Be(10123);
        (await Escalar(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.RespaldoVinculosIdentidad_172")).Should().Be(2);
    }

    private static async Task<long?> Escalar(string cadena, string sql) =>
        Convert.ToInt64(await clsBaseDatosPruebas.EscalarAsync<object>(cadena, sql), System.Globalization.CultureInfo.InvariantCulture);
}
