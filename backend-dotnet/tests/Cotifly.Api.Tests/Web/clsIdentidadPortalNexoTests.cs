using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Cotifly y Nexo resuelven la identidad del portal contra la misma base (en QA, <c>dbSolucionesTI_Prueba</c>). ClientePro
/// manda el NOMBRE de usuario (<c>idUsuario: "dguerra"</c>) y sus roles; Cotifly busca por <c>NOMBREUSUARIO</c>, guarda el <c>ID</c>
/// de esa fila en <c>UsuariosWeb.idUsuarioExterno</c> y ese mismo número es el que viaja a Nexo en <c>X-Portal-clsUsuario-Id</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsIdentidadPortalNexoTests : IDisposable
{
    /// <summary>ID de la persona en el portal (ejemplo: dguerra es 10123 en <c>dbSolucionesTI_Prueba</c>).</summary>
    private const long IdPortal = 10123;

    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsIdentidadPortalNexoTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElLoginPorNombreDeUsuarioGuardaElIdDeLaFilaYEseEsElQueViajaANexo()
    {
        // La fila del portal: NOMBREUSUARIO en mayúsculas, ID NUMERIC 10123 y roles en la base (25 y 34 sin mapeo: el 34 es AdministracionZonasGeocerca; + el de comercial).
        await _entorno.BaseDatos.CrearPersonaExternaConIdAsync(IdPortal, "DGUERRA", "Persona de prueba 172", "persona172@example.com",
            25, 34, clsBaseDatosPruebas.RolExternoComercial);

        // Lo que manda ClientePro: el nombre de usuario (no el ID) y sus roles informados (solo se auditan).
        using var login = await _api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro",
            new { idUsuario = "dguerra", roles = new[] { "25", "34", "32" } }, Ct);
        var cuerpoLogin = await login.Content.ReadAsStringAsync(Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK, cuerpoLogin);
        using var json = JsonDocument.Parse(cuerpoLogin);
        var access = json.RootElement.GetProperty("access").GetString()!;
        var usuarioId = json.RootElement.GetProperty("user").GetProperty("id").GetString();
        json.RootElement.GetProperty("user").GetProperty("rol").GetString().Should().Be("comercial");

        var guardado = await clsBaseDatosPruebas.EscalarAsync<long?>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE id = '{usuarioId}';");
        guardado.Should().Be(IdPortal, "idUsuarioExterno es el ID de la fila de tblSAPUsuariosApp encontrada por NOMBREUSUARIO");

        using var envio = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", access, clsCosteoEndpointsTests.Formulario());
        envio.StatusCode.Should().Be(HttpStatusCode.Created, await envio.Content.ReadAsStringAsync(Ct));

        _nexo.Peticiones.Should().NotBeEmpty()
            .And.OnlyContain(p => p.IdPortalUsuario == "10123", "toda petición a Nexo identifica al comercial con el ID del portal, no con su nombre");
        _nexo.Peticiones.Should().Contain(p => p.Metodo == HttpMethod.Post, "la solicitud de costeo llega a Nexo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConElVinculoSueltoElLoginYElRefreshVinculanAlNuevoIdDelPortal(bool porRefresh)
    {
        // Persona que entró con la base de identidad vieja: queda vinculada al ID viejo.
        var nombre = $"r{Guid.NewGuid():N}"[..20];
        var idViejo = await _entorno.BaseDatos.CrearPersonaExternaAsync(nombre, "Persona 172", $"{nombre}@example.com", clsBaseDatosPruebas.RolExternoComercial);
        var (usuarioId, refresh) = await LoginAsync(nombre);
        (await VinculoAsync(usuarioId)).Should().Be(idViejo);

        // Cambio de base (dbsolucionesti → dbSolucionesTI_Prueba): la misma persona tiene otro ID en la base nueva.
        var idNuevo = clsBaseDatosPruebas.NuevoIdExterno();
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaIdentidad,
            $"UPDATE dbo.tblSAPUsuariosApp SET ID = {idNuevo} WHERE ID = {idViejo}; UPDATE dbo.tblSAPUsuarioRoles SET IDUSUARIO = {idNuevo} WHERE IDUSUARIO = {idViejo};");
        using (var rechazado = await _api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre }, Ct))
        {
            rechazado.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "con el vínculo viejo el login es identidad_inconsistente");
        }

        // Lo que hace db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql, solo para esta persona.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.UsuariosWeb SET idUsuarioExterno = NULL WHERE id = '{usuarioId}';");

        if (porRefresh)
        {
            using var respuesta = await _api.Cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh }, Ct);
            respuesta.StatusCode.Should().Be(HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync(Ct));
        }
        else
        {
            (await LoginAsync(nombre)).UsuarioId.Should().Be(usuarioId, "es la misma cuenta, no una nueva");
        }

        (await VinculoAsync(usuarioId)).Should().Be(idNuevo);
    }

    private async Task<(string UsuarioId, string Refresh)> LoginAsync(string nombre)
    {
        using var respuesta = await _api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre }, Ct);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(Ct);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK, cuerpo);
        using var json = JsonDocument.Parse(cuerpo);
        return (json.RootElement.GetProperty("user").GetProperty("id").GetString()!, json.RootElement.GetProperty("refresh").GetString()!);
    }

    private Task<long?> VinculoAsync(string usuarioId) =>
        clsBaseDatosPruebas.EscalarAsync<long?>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE id = '{usuarioId}';");

    public void Dispose() => _api.Dispose();
}
