using System.Net;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-044.4 (#255, ADR-029): <c>POST /web/licitaciones/normalizar-valores</c> por HTTP contra SQL Server real y el Nexo falso, con el
/// cuerpo validado contra <c>specs/openapi.yaml</c>: alias, diccionario, ciudad ambigua con candidatos, sin resolver, IA no disponible,
/// tope diario de IA, la línea de negocio traducida a la de Cotifly y la validación local sin llamar a NEXO.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsNormalizacionValoresEndpointsTests : IDisposable
{
    private const string Ruta = "/web/licitaciones/normalizar-valores";
    private const string RutaNexo = "/api/captura-solicitudes/normalizar-valores";
    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsNormalizacionValoresEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
        _nexo.ValoresNormalizados.AddRange(
        [
            new("carroceria", "Sencillo furgonado", "resuelto", "FURGON", "catalogo", Descripcion: "Furgón"),
            new("carroceria", "Volqueta", "resuelto", "VOLCO", "catalogo", Descripcion: "Volco"),
            new("tipoVehiculo", "TB", "resuelto", "2L2", "alias", Descripcion: "Turbo"),
            new("ciudad", "La Union", "ambiguo", null, "catalogo", 0.5,
                Candidatos: [(5400, "05400", "La Unión", "Antioquia"), (52399, "52399", "La Unión", "Nariño")]),
            new("ciudad", "Buenaventura", "resuelto", "76109", "catalogo", Descripcion: "Buenaventura", MunicipioId: 76109),
        ]);
    }

    private static string Cuerpo(string? nit, params (string Campo, string Valor)[] valores) => JsonSerializer.Serialize(new
    {
        nit_cliente = nit,
        valores = valores.Select(v => new { campo = v.Campo, valor = v.Valor }),
    });

    private async Task<JsonElement> PostAsync(string cuerpo, HttpStatusCode esperado)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);
        return await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, esperado);
    }

    private JsonNode UltimoCuerpoANexo() =>
        JsonNode.Parse(_nexo.Peticiones.Last(p => p.Url.AbsolutePath == RutaNexo).Cuerpo!)!;

    [Fact]
    public async Task ResuelvePorAliasYPorDiccionarioConLaIdentidadYElNit()
    {
        var json = await PostAsync(Cuerpo("900.123.456-7", ("tipo_vehiculo", "TB"), ("tipo_vehiculo", "Tractomula")), HttpStatusCode.OK);

        json.GetProperty("valores").EnumerateArray()
            .Select(v => (v.GetProperty("campo").GetString(), v.GetProperty("valor").GetString(), v.GetProperty("estado").GetString(),
                v.GetProperty("codigo").GetString(), v.GetProperty("origen").GetString()))
            .Should().Equal(("tipo_vehiculo", "TB", "resuelto", "2L2", "alias"), ("tipo_vehiculo", "Tractomula", "resuelto", "3S3", "diccionario"));
        json.GetProperty("ia_usada").GetBoolean().Should().BeFalse();
        json.GetProperty("motivo_sin_ia").GetString().Should().Be("SIN_PENDIENTES");
        var enviado = UltimoCuerpoANexo();
        enviado["clienteNit"]!.GetValue<string>().Should().Be("900123456");
        enviado["valores"]!.AsArray().Select(v => v!["campo"]!.GetValue<string>()).Should().AllBe("tipoVehiculo");
        _nexo.Peticiones.Last(p => p.Url.AbsolutePath == RutaNexo).IdPortalUsuario.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task HU0448_LasCarroceriasQueProponeNormalizarExistenEnTiposCarroceria()
    {
        // HU-044.8 (#291): normalizar-valores propone carrocerías del catálogo de NEXO; GET /web/tipos-carroceria sale del mismo catálogo,
        // así que todo código que proponga está en el selector.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var normalizadas = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access,
            Cuerpo("900123456", ("carroceria", "Sencillo furgonado"), ("carroceria", "Volqueta")));
        var propuestas = (await VerificarContratoAsync(normalizadas, Ruta, HttpMethod.Post, HttpStatusCode.OK))
            .GetProperty("valores").EnumerateArray().Select(v => v.GetProperty("codigo").GetString()).ToList();

        using var selector = await _api.EnviarAsync(HttpMethod.Get, "/web/tipos-carroceria", sesion.Access);
        var catalogo = (await VerificarContratoAsync(selector, "/web/tipos-carroceria", HttpMethod.Get, HttpStatusCode.OK))
            .EnumerateArray().Select(c => c.GetProperty("codigo").GetString()).ToList();

        propuestas.Should().Equal("FURGON", "VOLCO");
        propuestas.Should().OnlyContain(c => catalogo.Contains(c), "cada carrocería propuesta existe en GET /web/tipos-carroceria");
    }

    [Fact]
    public async Task UnaCiudadAmbiguaTraeCandidatosYUnaResueltaElMunicipioDeNexo()
    {
        var json = await PostAsync(Cuerpo(null, ("ciudad", "La Union"), ("ciudad", "Buenaventura")), HttpStatusCode.OK);

        var valores = json.GetProperty("valores");
        valores[0].GetProperty("estado").GetString().Should().Be("ambiguo");
        valores[0].GetProperty("candidatos").EnumerateArray().Select(c => (c.GetProperty("municipio_id").GetInt32(), c.GetProperty("departamento").GetString()))
            .Should().Equal((5400, "Antioquia"), (52399, "Nariño"));
        valores[1].GetProperty("municipio_id").GetInt32().Should().Be(76109);
        UltimoCuerpoANexo()["clienteNit"].Should().BeNull("sin NIT NEXO usa solo el diccionario general");
    }

    [Fact]
    public async Task SinResolverConLaIaDeNexoNoDisponibleEs200ConLoQueResolvieronLasReglas()
    {
        _nexo.IaImportacionDisponible = false;

        var json = await PostAsync(Cuerpo("900123456", ("tipo_vehiculo", "TB"), ("carroceria", "Cosa rara")), HttpStatusCode.OK);

        json.GetProperty("ia_usada").GetBoolean().Should().BeFalse();
        json.GetProperty("motivo_sin_ia").GetString().Should().Be("IA_NO_DISPONIBLE");
        json.GetProperty("valores").EnumerateArray().Select(v => v.GetProperty("estado").GetString()).Should().Equal("resuelto", "sin_resolver");
    }

    [Fact]
    public async Task LaLineaDeNegocioLlegaComoLaLineaDeCotifly()
    {
        var json = await PostAsync(Cuerpo("900123456", ("linea_negocio", "impo"), ("linea_negocio", "Distribución")), HttpStatusCode.OK);

        json.GetProperty("valores").EnumerateArray()
            .Select(v => (v.GetProperty("estado").GetString(), v.GetProperty("codigo").GetString(), v.GetProperty("origen").GetString(),
                v.GetProperty("linea_nexo").GetString()))
            .Should().Equal(("resuelto", "Importacion", "sinonimo", "Puerto a interior"), ("resuelto", "Distribucion", "linea_cotifly", null));
        UltimoCuerpoANexo()["valores"]!.AsArray().Select(v => (v!["campo"]!.GetValue<string>(), v["valor"]!.GetValue<string>()))
            .Should().Equal([("lineaNegocio", "impo")], "«Distribución» es una línea de Cotifly: no se le pregunta a NEXO");
    }

    [Fact]
    public async Task ElTopeDiarioDeIaEs429()
    {
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "IA_TOPE_DIARIO";

        var json = await PostAsync(Cuerpo("900123456", ("ciudad", "Cali")), (HttpStatusCode)429);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("IA_TOPE_DIARIO");
    }

    [Fact]
    public async Task NexoCaidoEs503NexoNoDisponible()
    {
        _nexo.ImportacionFalla = HttpStatusCode.InternalServerError;

        var json = await PostAsync(Cuerpo("900123456", ("ciudad", "Cali")), HttpStatusCode.ServiceUnavailable);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
    }

    [Theory]
    [InlineData("""{"valores":[]}""", "VALORES_REQUERIDOS")]
    [InlineData("""{"valores":[{"campo":"clase_contenedor","valor":"40HC"}]}""", "CAMPO_INVALIDO")]
    [InlineData("""{"valores":[{"campo":"ciudad","valor":"   "}]}""", "VALORES_REQUERIDOS")]
    public async Task UnaPeticionInvalidaEs400SinLlamarANexo(string cuerpo, string codigo)
    {
        var json = await PostAsync(cuerpo, HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath == RutaNexo);
    }

    [Fact]
    public async Task UnSinPermisoDeNexoEs403NexoSinPermiso()
    {
        _nexo.ImportacionFalla = HttpStatusCode.Forbidden;
        _nexo.CodigoImportacionFalla = "SIN_PERMISO";

        var json = await PostAsync(Cuerpo("900123456", ("ciudad", "Cali")), HttpStatusCode.Forbidden);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_SIN_PERMISO");
    }

    [Fact]
    public async Task ElCupoPorMinutoDeNexoEs503NoDisponibleYNoElTopeDiario()
    {
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "DEMASIADAS_SOLICITUDES";

        var json = await PostAsync(Cuerpo("900123456", ("ciudad", "Cali")), HttpStatusCode.ServiceUnavailable);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
    }

    [Fact]
    public async Task UnNitInvalidoEs400AunqueTodoSeResuelvaEnCotifly()
    {
        var json = await PostAsync(Cuerpo(new string('9', 31), ("linea_negocio", "Nacional")), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CLIENTE_NIT_INVALIDO");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath == RutaNexo);
    }

    [Fact]
    public async Task UnRolQueNoEsComercialEs403()
    {
        // Token válido (firma, emisor y audiencia de la API) con un rol fuera de la política Comercial (comercial, gerente, director).
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "cotifly-api",
            Audience = "clientepro",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["jti"] = Guid.NewGuid().ToString(), ["rol"] = "consulta" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clsApiWebPruebas.ClaveJwt)), SecurityAlgorithms.HmacSha256),
        });

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, token, Cuerpo(null, ("ciudad", "Cali")));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath == RutaNexo);
    }

    [Fact]
    public async Task SinSesionEs401()
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, null, Cuerpo(null, ("ciudad", "Cali")));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
