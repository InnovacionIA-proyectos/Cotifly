using System.Net;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>GET /web/municipios</c> con Nexo sin responder: 503 <c>MUNICIPIOS_NO_DISPONIBLE</c>, como
/// <c>GET /web/catalogos-flete</c> y la captura, y no 400. Por HTTP contra SQL Server real y el Nexo falso, con el cuerpo validado
/// contra <c>specs/openapi.yaml</c>. Sin caché de municipios, para que cada petición vaya a Nexo.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMunicipiosEndpointsTests : IDisposable
{
    private const string Ruta = "/web/municipios";
    private const string Codigo = "MUNICIPIOS_NO_DISPONIBLE";
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsMunicipiosEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo,
            extra: new Dictionary<string, string?> { ["Nexo:MinutosCacheMunicipios"] = "0" });
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "?q=yum")]
    [InlineData(HttpStatusCode.BadGateway, "?q=la%20union&departamento=narino")]
    public async Task ConNexoCaidoRespondeServicioNoDisponibleYNoBadRequest(HttpStatusCode falla, string consulta)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.MunicipiosFalla = falla;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta + consulta, sesion.Access);

        await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, Codigo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=bog")]
    public async Task ConTiempoAgotadoEnNexoRespondeServicioNoDisponible(string consulta)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        // Así llega el tiempo agotado de HttpClient: un TaskCanceledException que no pidió quien llama.
        _nexo.MunicipiosExcepcion = new TaskCanceledException("Tiempo agotado (prueba).", new TimeoutException());

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta + consulta, sesion.Access);

        await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, Codigo);
    }

    [Fact]
    public async Task Un429DeNexoSigueSiendoNexoNoDisponible()
    {
        // Un 429 de Nexo lo tratan los adaptadores como "Nexo no disponible", nunca como un rechazo de la petición del comercial. En
        // este endpoint es 503, como en GET /web/catalogos-flete.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.MunicipiosFalla = HttpStatusCode.TooManyRequests;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta + "?q=cal", sesion.Access);

        await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, Codigo);
    }

    [Fact]
    public async Task SinNexoConfiguradoRespondeServicioNoDisponible()
    {
        var api = _entorno.ApiSinCorreo;
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(HttpMethod.Get, Ruta, sesion.Access);

        await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, Codigo);
    }

    [Fact]
    public async Task ConNexoArribaYFiltrosLibresResponde200()
    {
        // q y departamento son texto libre (se recortan a 100 caracteres): no hay entrada que sea un 400.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta + "?q=" + new string('x', 150), sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        json.GetArrayLength().Should().Be(0);

        using var busqueda = await _api.EnviarAsync(HttpMethod.Get, Ruta + "?q=yum", sesion.Access);
        var encontrados = await VerificarContratoAsync(busqueda, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        encontrados.EnumerateArray().Select(m => m.GetProperty("municipio").GetString()).Should().Contain("Yumbo");
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
