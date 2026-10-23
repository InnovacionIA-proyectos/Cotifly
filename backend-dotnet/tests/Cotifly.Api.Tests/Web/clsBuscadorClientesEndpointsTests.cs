using System.Net;
using System.Text.Json;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// OBS-1021B-3 (E2E 2026-10-21, punto 4.2 del QA): <c>GET /web/clientes</c> sin Nexo responde 503 <c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c>
/// (antes 400), como los demás catálogos de Nexo, con un mensaje que dice qué falta. Por HTTP contra SQL Server real y el Nexo falso, con
/// el cuerpo validado contra <c>specs/openapi.yaml</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsBuscadorClientesEndpointsTests(clsEntornoApiWeb entorno) : IDisposable
{
    private const string Ruta = "/web/clientes";
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente("900123456", "LONGYIN S.A.S.");
    private readonly List<clsApiWebPruebas> _apis = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConNexoRespondiendoDevuelveLosCandidatos()
    {
        var api = Registrar(clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo));
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(HttpMethod.Get, $"{Ruta}?q=longyin", sesion.Access);

        (await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK)).GetProperty("candidatos").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ConNexoCaidoEs503ClienteBusquedaNoDisponible()
    {
        var api = Registrar(clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo));
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await api.EnviarAsync(HttpMethod.Get, $"{Ruta}?q=longyin", sesion.Access);

        var error = (await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("CLIENTE_BUSQUEDA_NO_DISPONIBLE");
        error.GetProperty("mensaje").GetString().Should().Be(clsBuscadorClientesNexo.Mensaje);
    }

    [Fact]
    public async Task SinNexoConfiguradoEs503YElMensajeDiceQueFaltaNexoUrlBase()
    {
        var api = Registrar(new clsApiWebPruebas(entorno.BaseDatos));
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(HttpMethod.Get, $"{Ruta}?q=longyin", sesion.Access);

        var error = (await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("CLIENTE_BUSQUEDA_NO_DISPONIBLE");
        error.GetProperty("mensaje").GetString().Should().Contain("Nexo:UrlBase");
    }

    [Fact]
    public async Task SinTextoSigueSiendo400ErrorWeb()
    {
        var api = Registrar(clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo));
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(HttpMethod.Get, $"{Ruta}?q=%20", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetString().Should().Be("Falta el texto de búsqueda (q).");
    }

    private clsApiWebPruebas Registrar(clsApiWebPruebas api)
    {
        _apis.Add(api);
        return api;
    }

    public void Dispose()
    {
        foreach (var api in _apis)
        {
            api.Dispose();
        }

        _nexo.Dispose();
    }
}
