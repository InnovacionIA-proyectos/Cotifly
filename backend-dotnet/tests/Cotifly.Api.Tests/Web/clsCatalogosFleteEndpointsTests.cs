using System.Net;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-027.1 (#179): <c>GET /web/catalogos-flete</c> expone <c>clases_contenedor</c> con los valores de Nexo
/// (<c>GET /api/catalogos/clases-contenedor</c>, cuya <c>descripcion</c> es el código), sin la lista provisional G15, y con la lista
/// vacía cuando Nexo responde <c>[]</c> o ese catálogo falla (200, nunca 503). Por HTTP contra SQL Server real y el Nexo falso, con el
/// cuerpo validado contra <c>specs/openapi.yaml</c>. Sin caché de catálogos, para que cada petición vaya a Nexo.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCatalogosFleteEndpointsTests : IDisposable
{
    private const string Ruta = "/web/catalogos-flete";
    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsCatalogosFleteEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo,
            extra: new Dictionary<string, string?> { ["Nexo:MinutosCacheCatalogos"] = "0" });
    }

    [Fact]
    public async Task ExponeLasClasesDeContenedorDeNexoConElCodigoComoValorYSinLasProvisionales()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta, sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);

        var clases = json.GetProperty("clases_contenedor").EnumerateArray()
            .Select(c => (Valor: c.GetProperty("valor").GetString(), Etiqueta: c.GetProperty("etiqueta").GetString())).ToList();
        clases.Select(c => c.Valor).Should().Equal("CNT20.IT", "CNT20.RE", "CNT20.ST", "CNT40.HC", "CNT40.RE");
        clases.Should().OnlyContain(c => c.Valor == c.Etiqueta);
        clases.Select(c => c.Valor).Should().NotContain(["CNT20.FT", "CNT20.OP", "CNT40.FT", "CNT40.OP"]);
    }

    [Fact]
    public async Task ConLasClasesDeContenedorVaciasEnNexoResponde200ConLaListaVacia()
    {
        // Nexo responde [] cuando su base de SAP no está configurada.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.ClasesContenedor.Clear();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta, sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);

        json.GetProperty("clases_contenedor").GetArrayLength().Should().Be(0);
        json.GetProperty("clases_viaje").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ConElCatalogoDeClasesDeContenedorCaidoResponde200ConLaListaVacia()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.CatalogosFallidos.Add("clases-contenedor");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta, sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);

        json.GetProperty("clases_contenedor").GetArrayLength().Should().Be(0);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
