using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// #172: Cotifly y Nexo resuelven la identidad del portal contra la misma base (en QA, <c>dbSolucionesTI_Prueba</c>). ClientePro
/// manda el NOMBRE de usuario (<c>idUsuario: "dguerra"</c>) y sus roles; Cotifly busca por <c>NOMBREUSUARIO</c>, guarda el <c>ID</c>
/// de esa fila en <c>UsuariosWeb.idUsuarioExterno</c> y ese mismo número es el que viaja a Nexo en <c>X-Portal-Usuario-Id</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsIdentidadPortalNexoTests : IDisposable
{
    /// <summary>ID de la persona en el portal (ejemplo de #172: dguerra es 10123 en <c>dbSolucionesTI_Prueba</c>).</summary>
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
        // La fila del portal: NOMBREUSUARIO en mayúsculas, ID NUMERIC 10123 y roles en la base (25 y 31 sin mapeo + el de comercial).
        await _entorno.BaseDatos.CrearPersonaExternaConIdAsync(IdPortal, "DGUERRA", "Persona de prueba 172", "persona172@example.com",
            25, 31, clsBaseDatosPruebas.RolExternoComercial);

        // Lo que manda ClientePro: el nombre de usuario (no el ID) y sus roles informados (solo se auditan).
        using var login = await _api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro",
            new { idUsuario = "dguerra", roles = new[] { "25", "31", "32" } }, Ct);
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

    public void Dispose() => _api.Dispose();
}
