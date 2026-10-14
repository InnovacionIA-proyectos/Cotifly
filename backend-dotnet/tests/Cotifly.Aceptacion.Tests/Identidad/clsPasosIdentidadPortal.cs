using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Costeo;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Identidad;

/// <summary>
/// Pasos de <c>specs/features/identidad_portal.feature</c> (#172): login por HTTP con el NOMBRE de usuario, el ID del portal en
/// <c>UsuariosWeb.idUsuarioExterno</c> y en <c>X-Portal-clsUsuario-Id</c> de cada petición al Nexo falso de <see cref="clsPasosCosteo"/>, y la
/// revinculación con el vínculo en NULL (lo que hace <c>db/scripts/operacion/revincular_…sql</c>, solo para la persona del escenario).
/// Cada alias se traduce a un nombre y un ID únicos: la base de identidad se comparte entre escenarios.
/// </summary>
[Binding]
public sealed class clsPasosIdentidadPortal(clsPasosCosteo costeo, clsContextoWeb web)
{
    private readonly string _sufijo = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, long> _idsPortal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _cuentas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rolEmitido = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _cuentaUltimoLogin = new(StringComparer.OrdinalIgnoreCase);
    private HttpStatusCode _ultimoLogin;
    private HttpStatusCode _ultimoRefresh;

    private static clsBaseDatosPruebas BaseDatos => clsEntornoAceptacion.BaseDatos;

    private string Nombre(string alias) => $"{alias}{_sufijo}".ToLowerInvariant();

    [Given("que en el portal {string} está registrado en mayúsculas con un ID propio y el rol externo de comercial")]
    public async Task DadoLaPersonaDelPortal(string alias)
    {
        var id = clsBaseDatosPruebas.NuevoIdExterno();
        await BaseDatos.CrearPersonaExternaConIdAsync(id, Nombre(alias).ToUpperInvariant(), $"Persona {alias}", $"{Nombre(alias)}@example.com",
            25, clsBaseDatosPruebas.RolExternoComercial);
        _idsPortal[alias] = id;
    }

    [Given("ClientePro inicia la sesión de {string} con su nombre de usuario en minúsculas")]
    [When("ClientePro inicia la sesión de {string} con su nombre de usuario en minúsculas")]
    public async Task CuandoIniciaSesion(string alias)
    {
        // Lo que manda ClientePro: el nombre de usuario (no el ID); los roles del cuerpo nunca deciden.
        using var respuesta = await web.Api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = Nombre(alias), roles = new[] { "25" } });
        _ultimoLogin = respuesta.StatusCode;
        if (respuesta.StatusCode != HttpStatusCode.OK)
        {
            return;
        }

        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        var usuario = json.RootElement.GetProperty("user");
        var cuenta = Guid.Parse(usuario.GetProperty("id").GetString()!);
        _cuentas.TryAdd(alias, cuenta);
        _refresh[alias] = json.RootElement.GetProperty("refresh").GetString()!;
        web.Sesiones[alias] = new clsSesionPrueba(json.RootElement.GetProperty("access").GetString()!, cuenta, Nombre(alias));
        _rolEmitido[alias] = usuario.GetProperty("rol").GetString()!;
        _cuentaUltimoLogin[alias] = cuenta;
    }

    [Then("la sesión de {string} se emite con el rol {string}")]
    public void EntoncesElRol(string alias, string rol)
    {
        _ultimoLogin.Should().Be(HttpStatusCode.OK);
        _rolEmitido[alias].Should().Be(rol);
    }

    [Then("Cotifly guarda el ID del portal de {string} como su identidad externa")]
    public async Task EntoncesGuardaElIdDelPortal(string alias) =>
        (await clsBaseDatosPruebas.EscalarAsync<long?>(BaseDatos.CadenaCotifly,
            $"SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE id = '{_cuentas[alias]}';"))
            .Should().Be(_idsPortal[alias], "idUsuarioExterno es el ID de la fila del portal encontrada por NOMBREUSUARIO");

    [Then("toda petición que recibió Nexo lleva el ID del portal de {string} en X-Portal-clsUsuario-Id")]
    public void EntoncesElEncabezado(string alias)
    {
        var id = _idsPortal[alias].ToString(System.Globalization.CultureInfo.InvariantCulture);
        costeo.Nexo.Peticiones.Should().NotBeEmpty()
            .And.OnlyContain(p => p.IdPortalUsuario == id, "toda petición a Nexo identifica al comercial con el ID del portal, no con su nombre");
        costeo.Nexo.Peticiones.Should().Contain(p => p.Metodo == HttpMethod.Post, "la solicitud de costeo llegó a Nexo");
    }

    [When("la base de identidad cambia y {string} tiene otro ID en el portal")]
    public async Task CuandoCambiaElId(string alias)
    {
        // Cambio de base (dbsolucionesti → dbSolucionesTI_Prueba): la misma persona tiene otro ID en la base nueva.
        var viejo = _idsPortal[alias];
        var nuevo = clsBaseDatosPruebas.NuevoIdExterno();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaIdentidad,
            $"UPDATE dbo.tblSAPUsuariosApp SET ID = {nuevo} WHERE ID = {viejo}; UPDATE dbo.tblSAPUsuarioRoles SET IDUSUARIO = {nuevo} WHERE IDUSUARIO = {viejo};");
        _idsPortal[alias] = nuevo;
    }

    [Then("el login de {string} responde credenciales inválidas")]
    public async Task EntoncesElLoginFalla(string alias)
    {
        await CuandoIniciaSesion(alias);
        _ultimoLogin.Should().Be(HttpStatusCode.Unauthorized, "con el vínculo viejo el login es identidad_inconsistente");
    }

    [When("el DBA suelta el vínculo de identidad de {string}")]
    public Task CuandoElDbaSueltaElVinculo(string alias) =>
        clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.UsuariosWeb SET idUsuarioExterno = NULL WHERE id = '{_cuentas[alias]}';");

    [Then("la sesión de {string} es la misma cuenta de Cotifly")]
    public void EntoncesLaMismaCuenta(string alias)
    {
        _ultimoLogin.Should().Be(HttpStatusCode.OK);
        _cuentaUltimoLogin[alias].Should().Be(_cuentas[alias], "la revinculación usa la misma cuenta, no crea una nueva");
    }

    [When("{string} renueva su sesión con el refresh vigente")]
    public async Task CuandoRenueva(string alias)
    {
        using var respuesta = await web.Api.Cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = _refresh[alias] });
        _ultimoRefresh = respuesta.StatusCode;
    }

    [Then("el refresh de {string} responde {int}")]
    public void EntoncesElRefresh(string alias, int estado) =>
        _ultimoRefresh.Should().Be((HttpStatusCode)estado, $"el refresh de {alias}");
}
