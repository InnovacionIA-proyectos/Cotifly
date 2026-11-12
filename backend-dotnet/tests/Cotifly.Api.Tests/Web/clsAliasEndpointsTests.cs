using System.Net;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-031.1 (#194): <c>GET /web/alias/resolver</c> resuelve contra el catálogo y el diccionario de alias de Nexo (NEXO #321), en lote, por
/// HTTP contra SQL Server real y el Nexo falso, con el cuerpo validado contra <c>specs/openapi.yaml</c>. Sin caché, para que cada petición
/// vaya a Nexo.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAliasEndpointsTests : IDisposable
{
    private const string Ruta = "/web/alias/resolver";
    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsAliasEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo,
            extra: new Dictionary<string, string?> { ["Nexo:MinutosCacheCatalogos"] = "0" });
        _nexo.Alias.AddRange(
        [
            ("TB", "Vehiculo", "2S3", true), ("C2", "Vehiculo", "2", true), ("Viejo", "Vehiculo", "3S3", false),
            ("40 HC", "Contenedor", "CNT40.HC", true),
        ]);
    }

    [Fact]
    public async Task ResuelveVariosVehiculosEnUnaLlamadaConLaFuente()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?tipo=vehiculo&texto=tb&texto=3s3&texto=C2&texto=Viejo&texto=Tractomula", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);

        json.GetProperty("tipo").GetString().Should().Be("vehiculo");
        json.GetProperty("resultados").EnumerateArray()
            .Select(r => (r.GetProperty("texto").GetString(), r.GetProperty("reconocido").GetBoolean(), r.GetProperty("codigo").GetString(), r.GetProperty("fuente").GetString()))
            // HU-044.8 (#291): C2 es el código NEXO canónico; la equivalencia de SICE-TAC lo resuelve como «catalogo», no vía alias a «2».
            .Should().Equal(("tb", true, "2S3", "alias"), ("3s3", true, "3S3", "catalogo"), ("C2", true, "C2", "catalogo"), ("Viejo", false, null, null),
                ("Tractomula", false, null, null));
        _nexo.Peticiones.Count(p => p.Url.AbsolutePath == "/api/catalogos/alias").Should().Be(1, "una sola petición para todo el lote");
    }

    [Fact]
    public async Task ResuelveClasesDeContenedor()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?tipo=contenedor&texto=40-hc&texto=CNT20.FT&texto=TB", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);

        json.GetProperty("resultados").EnumerateArray().Select(r => r.GetProperty("codigo").GetString())
            .Should().Equal("CNT40.HC", "CNT20.FT", null);
    }

    [Theory]
    [InlineData("?tipo=camion&texto=TB")]
    [InlineData("?tipo=vehiculo")]
    [InlineData("?texto=TB")]
    public async Task UnaPeticionInvalidaEs400(string consulta)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, Ruta + consulta, sesion.Access);
        await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConElDiccionarioCaidoEs503YNoResponde_NoReconocido()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.CatalogosFallidos.Add("alias");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?tipo=vehiculo&texto=TB", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.ServiceUnavailable);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CATALOGOS_FLETE_NO_DISPONIBLE");
    }

    [Fact]
    public async Task SinSesionEs401()
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?tipo=vehiculo&texto=TB", null);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
