using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Identidad.Repositories;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-037.1 (#209) y HU-037.2 (#210): <c>db/scripts/15_rol_director.sql</c> (CHECK de <c>Roles</c> con <c>director</c> y la fila sin rol
/// externo), el script de operación de prueba <c>db/scripts/operacion/mapeo_prueba_rol_director_209.sql</c> (gerente 31 → 25 y luego director
/// → 31, por el índice único de <c>idRolExterno</c>) y el repositorio de roles, que ignora los códigos que este backend no conoce.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsRolDirectorScriptTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static string ScriptOperacion => clsBaseDatosPruebas.RutaScriptInstalacion(Path.Combine("operacion", "mapeo_prueba_rol_director_209.sql"));

    [Fact]
    public async Task ElScript15SobreUnaBaseSinElAgregaDirectorSinRolExternoYAdmiteElCodigo()
    {
        // La base migrada hasta el script anterior al 15: Roles del 0002 (comercial y gerente_comercial) con el CHECK de dos códigos.
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbDirector15a", clsBaseDatosPruebas.Scripts.Length - 1);
        var antes = () => clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "INSERT INTO dbo_cotifly.Roles (codigo) VALUES ('director')");
        (await antes.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_roles_codigo");

        await clsBaseDatosPruebas.AplicarScriptAsync(aislada.Cadena, clsBaseDatosPruebas.Script15);

        (await Escalar<string>(aislada.Cadena, "SELECT CONCAT(codigo, '|', COALESCE(CAST(idRolExterno AS nvarchar(10)), 'NULL'), '|', activo) FROM dbo_cotifly.Roles WHERE codigo = 'director'"))
            .Should().Be("director|NULL|1");
        (await Escalar<int>(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.Roles")).Should().Be(3);
        var inventado = () => clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "INSERT INTO dbo_cotifly.Roles (codigo) VALUES ('admin')");
        (await inventado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_roles_codigo");
    }

    [Fact]
    public async Task ReaplicarEl15NoPisaElRolExternoQueFijoElDbaNiDuplicaLaFila()
    {
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbDirector15b", clsBaseDatosPruebas.Scripts.Length);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "UPDATE dbo_cotifly.Roles SET idRolExterno = 31 WHERE codigo = 'director'");

        await clsBaseDatosPruebas.AplicarScriptAsync(aislada.Cadena, clsBaseDatosPruebas.Script15);

        (await Escalar<int>(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.Roles WHERE codigo = 'director'")).Should().Be(1);
        (await Escalar<int>(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'director'")).Should().Be(31);
    }

    [Fact]
    public async Task ElMapeoDePruebaMueveAlGerenteAl25YDespuesPoneAlDirectorEnEl31ConRespaldoYEsIdempotente()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbDirector209a");
        await RolesAsync(aislada.Cadena, comercial: 32, gerente: 31);

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, ScriptOperacion);

        (await Mapeo(aislada.Cadena)).Should().Be("comercial=32,director=31,gerente_comercial=25");
        (await Escalar<int>(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.RespaldoRoles_209 WHERE codigo = 'gerente_comercial'")).Should().Be(31);
        (await Escalar<int>(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.RespaldoRoles_209 WHERE codigo = 'director' AND idRolExterno IS NULL")).Should().Be(1);

        await clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, ScriptOperacion);

        (await Mapeo(aislada.Cadena)).Should().Be("comercial=32,director=31,gerente_comercial=25");
        (await Escalar<int>(aislada.Cadena, "SELECT idRolExterno FROM dbo_cotifly.RespaldoRoles_209 WHERE codigo = 'gerente_comercial'"))
            .Should().Be(31, "el respaldo es el de la primera ejecución");
    }

    [Fact]
    public async Task ElIndiceUnicoImpidePonerleEl31AlDirectorAntesDeSacarAlGerente()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbDirector209b");
        await RolesAsync(aislada.Cadena, comercial: 32, gerente: 31);

        var alReves = () => clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "UPDATE dbo_cotifly.Roles SET idRolExterno = 31 WHERE codigo = 'director'");

        (await alReves.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("uq_roles_idRolExterno");
    }

    [Fact]
    public async Task ElMapeoDePruebaFallaSinCambiarNadaSiOtroRolTieneEl25()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbDirector209c");
        await RolesAsync(aislada.Cadena, comercial: 25, gerente: 31);

        var correr = () => clsBaseDatosPruebas.EjecutarScriptAsync(aislada.Cadena, ScriptOperacion);

        await correr.Should().ThrowAsync<SqlException>();
        (await Mapeo(aislada.Cadena)).Should().Be("comercial=25,director=NULL,gerente_comercial=31");
    }

    [Fact]
    public async Task ElRepositorioIgnoraUnRolDelCatalogoQueEsteBackendNoConoce()
    {
        // Un rol futuro en Roles (como `director` para un backend anterior a HU-037) no tumba el login de todos: se ignora.
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbDirectorDesc");
        await RolesAsync(aislada.Cadena, comercial: 32, gerente: 31);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, """
            ALTER TABLE dbo_cotifly.Roles DROP CONSTRAINT ck_roles_codigo;
            INSERT INTO dbo_cotifly.Roles (codigo, descripcion, idRolExterno, activo) VALUES (N'auditor', N'Rol futuro', 50, 1);
            INSERT INTO dbo_cotifly.UsuariosWeb (id, nombreUsuario) VALUES ('6a1d6c0e-7f0a-4b8a-9a55-0b8f8d3c1e01', N'rol.futuro');
            INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId, activo)
                SELECT '6a1d6c0e-7f0a-4b8a-9a55-0b8f8d3c1e01', id, 1 FROM dbo_cotifly.Roles WHERE codigo = N'auditor';
            """);
        var opciones = new DbContextOptionsBuilder<clsCotiflyDbContext>().UseSqlServer(aislada.Cadena).Options;
        await using var contexto = new clsCotiflyDbContext(opciones);
        var logger = new clsLoggerFalso<clsRolRepository>();
        IClsRolRepository repositorio = new clsRolRepository(contexto, logger);
        var idAuditor = await Escalar<int>(aislada.Cadena, "SELECT id FROM dbo_cotifly.Roles WHERE codigo = N'auditor'");
        var usuario = Guid.Parse("6a1d6c0e-7f0a-4b8a-9a55-0b8f8d3c1e01");

        var activos = await repositorio.ListarActivosAsync(Ct);
        var asignados = await repositorio.ListarRolesAsignadosAsync(usuario, Ct);
        var porUsuario = await repositorio.ListarRolesDeUsuariosAsync([usuario], Ct);

        activos.Select(r => r.Codigo).Should().BeEquivalentTo([RolInterno.Comercial, RolInterno.GerenteComercial, RolInterno.Director]);
        asignados.Should().BeEmpty();
        porUsuario.Should().BeEmpty();
        // No pasa en silencio: el catálogo avisa el id del rol ignorado (validación HU-037).
        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Which.Mensaje.Should().Contain($"(id {idAuditor})").And.Contain("no conoce");
    }

    [Fact]
    public async Task ConElCatalogoConocidoNoHayAvisoDeRolesIgnorados()
    {
        await using var aislada = await BaseDatos.CrearBaseInstaladaAsync("dbDirectorSinAviso");
        await RolesAsync(aislada.Cadena, comercial: 32, gerente: 31);
        var opciones = new DbContextOptionsBuilder<clsCotiflyDbContext>().UseSqlServer(aislada.Cadena).Options;
        await using var contexto = new clsCotiflyDbContext(opciones);
        var logger = new clsLoggerFalso<clsRolRepository>();

        var activos = await new clsRolRepository(contexto, logger).ListarActivosAsync(Ct);

        activos.Should().HaveCount(3);
        logger.Registros.Should().BeEmpty();
    }

    /// <summary>"codigo=idRolExterno" de los tres roles, ordenado por código.</summary>
    private static Task<string> Mapeo(string cadena) => Escalar<string>(cadena,
        "SELECT STRING_AGG(CONCAT(codigo, '=', COALESCE(CAST(idRolExterno AS nvarchar(10)), 'NULL')), ',') WITHIN GROUP (ORDER BY codigo) FROM dbo_cotifly.Roles");

    /// <summary>Los roles como en la base compartida (la base aislada trae solo `director`, del 15: los demás los siembra el seeder).</summary>
    private static Task RolesAsync(string cadena, int comercial, int gerente) => clsBaseDatosPruebas.EjecutarAsync(cadena, $"""
        IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = 'comercial')
            INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'comercial', N'Comercial');
        IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = 'gerente_comercial')
            INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'gerente_comercial', N'Gerente comercial');
        UPDATE dbo_cotifly.Roles SET idRolExterno = NULL;
        UPDATE dbo_cotifly.Roles SET idRolExterno = {comercial} WHERE codigo = 'comercial';
        UPDATE dbo_cotifly.Roles SET idRolExterno = {gerente} WHERE codigo = 'gerente_comercial';
        """);

    private static async Task<T> Escalar<T>(string cadena, string sql) => (await clsBaseDatosPruebas.EscalarAsync<T>(cadena, sql))!;
}
