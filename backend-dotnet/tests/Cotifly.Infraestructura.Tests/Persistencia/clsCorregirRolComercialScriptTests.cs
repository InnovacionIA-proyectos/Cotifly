using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// #173: <c>db/scripts/operacion/corregir_rol_comercial_32.sql</c> en una base aislada instalada desde cero. Respalda <c>Roles</c>,
/// pasa <c>comercial</c> de 34 a 32 sin tocar <c>gerente_comercial</c> (31) y, si se repite, no cambia nada más.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCorregirRolComercialScriptTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static string Script => clsBaseDatosPruebas.RutaScriptInstalacion(Path.Combine("operacion", "corregir_rol_comercial_32.sql"));

    [Fact]
    public async Task PasaElComercialDe34A32ConRespaldoYEsIdempotente()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbRolComercial173");
        await RolesAsync(aislada.Cadena, comercial: 34, gerente: 31);

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, Script);

        (await Escalar(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'")).Should().Be(32);
        (await Escalar(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'gerente_comercial'")).Should().Be(31);
        (await Escalar(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.RespaldoRoles_173 WHERE codigo = 'comercial' AND respaldadoAt IS NOT NULL"))
            .Should().Be(34);
        (await Escalar(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.RespaldoRoles_173")).Should().Be(2);

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, Script);

        (await Escalar(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'")).Should().Be(32);
        (await Escalar(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.RespaldoRoles_173 WHERE codigo = 'comercial'")).Should().Be(34);
    }

    [Fact]
    public async Task NoPisaUnValorDistintoDe34()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbRolComercial173b");
        await RolesAsync(aislada.Cadena, comercial: 77, gerente: 31);

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, Script);

        (await Escalar(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'")).Should().Be(77);
    }

    /// <summary>Los dos roles como en la base compartida (la base aislada puede no traer la semilla de <c>Roles</c>).</summary>
    private static Task RolesAsync(string cadena, int comercial, int gerente) => clsBaseDatosPruebas.EjecutarAsync(cadena, $"""
        IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = 'comercial')
            INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'comercial', N'Comercial');
        IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = 'gerente_comercial')
            INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'gerente_comercial', N'Gerente comercial');
        UPDATE dbo_cotifly.Roles SET idRolExterno = NULL;
        UPDATE dbo_cotifly.Roles SET idRolExterno = {comercial} WHERE codigo = 'comercial';
        UPDATE dbo_cotifly.Roles SET idRolExterno = {gerente} WHERE codigo = 'gerente_comercial';
        """);

    private static async Task<long?> Escalar(string cadena, string sql) =>
        Convert.ToInt64(await clsBaseDatosPruebas.EscalarAsync<object>(cadena, sql), System.Globalization.CultureInfo.InvariantCulture);
}
