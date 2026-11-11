using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Validación adversarial de HU-044.4 (#255) por HTTP, contra SQL Server real y el Nexo falso: límites en el borde (1000/1001 valores,
/// 150/151 caracteres), cuerpos mal formados (nunca 500), errores de NEXO (403, 400 propio de NEXO, 429, 503), NIT opcional o inválido,
/// roles y que la petición a NEXO no lleve repetidos.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsNormalizacionValoresAdversarialEndpointsTests : IDisposable
{
    private const string Ruta = "/web/licitaciones/normalizar-valores";
    private const string RutaNexo = "/api/captura-solicitudes/normalizar-valores";
    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsNormalizacionValoresAdversarialEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static string Cuerpo(string? nit, IEnumerable<(string Campo, string Valor)> valores) => JsonSerializer.Serialize(new
    {
        nit_cliente = nit,
        valores = valores.Select(v => new { campo = v.Campo, valor = v.Valor }),
    });

    private async Task<JsonElement> PostAsync(string cuerpo, HttpStatusCode esperado, int rol = clsBaseDatosPruebas.RolExternoComercial)
    {
        var sesion = await _api.IniciarSesionAsync(rol);
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);
        return await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, esperado);
    }

    private IEnumerable<clsPeticionNexo> PeticionesANexo() => _nexo.Peticiones.Where(p => p.Url.AbsolutePath == RutaNexo);

    [Fact]
    public async Task MilValoresDe150CaracteresSonUn200ConMilResultadosEnOrden()
    {
        var valores = Enumerable.Range(0, 1000).Select(i => ("ciudad", $"{i:D4}" + new string('é', 146))).ToList();

        var json = await PostAsync(Cuerpo(null, valores), HttpStatusCode.OK);

        json.GetProperty("valores").EnumerateArray().Select(v => v.GetProperty("valor").GetString()).Should().Equal(valores.Select(v => v.Item2));
        JsonNode.Parse(PeticionesANexo().Single().Cuerpo!)!["valores"]!.AsArray().Should().HaveCount(1000);
    }

    [Fact]
    public async Task MilUnValoresEs400DemasiadosValoresSinLlamarANexo()
    {
        var valores = Enumerable.Range(0, 1001).Select(i => ("ciudad", $"C{i}"));

        var json = await PostAsync(Cuerpo(null, valores), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("DEMASIADOS_VALORES");
        PeticionesANexo().Should().BeEmpty();
    }

    [Theory]
    [InlineData(151, 'a')]
    [InlineData(151, 'ñ')]
    public async Task UnValorDe151CaracteresEs400ValorDemasiadoLargo(int largo, char c)
    {
        var json = await PostAsync(Cuerpo(null, [("tipo_vehiculo", new string(c, largo))]), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("VALOR_DEMASIADO_LARGO");
        PeticionesANexo().Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"valores":null}""")]
    [InlineData("""{"valores":[null]}""")]
    [InlineData("""{"valores":"Cali"}""")]
    [InlineData("""{"valores":[{"campo":"ciudad","valor":5}]}""")]
    [InlineData("""{"valores":[{"campo":7,"valor":"Cali"}]}""")]
    [InlineData("""{"valores":[{"valor":"Cali"}]}""")]
    [InlineData("""{"valores":[{"campo":"ciudad"}]}""")]
    [InlineData("""{"nit_cliente":123,"valores":[{"campo":"ciudad","valor":"Cali"}]}""")]
    [InlineData("""{"valores":[{"campo":"ciudad","valor":"Cali"}""")]
    public async Task UnCuerpoMalFormadoEs400NuncaUn500(string cuerpo)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest, await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        PeticionesANexo().Should().BeEmpty();
    }

    [Fact]
    public async Task SinPermisoEnNexoEs403NexoSinPermiso()
    {
        _nexo.ImportacionFalla = HttpStatusCode.Forbidden;
        _nexo.CodigoImportacionFalla = "FORBIDDEN";

        var json = await PostAsync(Cuerpo(null, [("ciudad", "Cali")]), HttpStatusCode.Forbidden);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_SIN_PERMISO");
    }

    [Fact]
    public async Task UnLimiteMasEstrictoDeNexoLlegaComo400ConSuCodigo()
    {
        // Si NEXO contara bytes y no caracteres, rechazaría lo que Cotifly dejó pasar: el front debe ver el código de NEXO, no un 503.
        _nexo.ImportacionFalla = HttpStatusCode.BadRequest;
        _nexo.CodigoImportacionFalla = "VALOR_DEMASIADO_LARGO";

        var json = await PostAsync(Cuerpo(null, [("ciudad", new string('é', 150))]), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("VALOR_DEMASIADO_LARGO");
    }

    [Fact]
    public async Task ElCupoDeIaAgotadoEs429AunqueHayaLineasDeCotiflyResueltas()
    {
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "IA_TOPE_DIARIO";

        var json = await PostAsync(Cuerpo(null, [("linea_negocio", "Distribución"), ("ciudad", "Cali")]), (HttpStatusCode)429);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("IA_TOPE_DIARIO");
    }

    [Theory]
    [InlineData("....")]
    [InlineData("-123")]
    [InlineData("1234567890123456789012345678901")]
    public async Task ConUnNitSinFormaEs400ClienteNitInvalidoSinLlamarANexo(string nit)
    {
        var json = await PostAsync(Cuerpo(nit, [("ciudad", "Cali")]), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CLIENTE_NIT_INVALIDO");
        PeticionesANexo().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SinNitOConNitVacioNoViajaClienteNit(string? nit)
    {
        await PostAsync(Cuerpo(nit, [("ciudad", "Cali")]), HttpStatusCode.OK);

        JsonNode.Parse(PeticionesANexo().Single().Cuerpo!)!["clienteNit"].Should().BeNull();
    }

    [Fact]
    public async Task LosRepetidosNoViajanANexoYCadaUnoTieneSuResultado()
    {
        var json = await PostAsync(Cuerpo(null, [("ciudad", "Cali"), ("ciudad", " Cali "), ("ciudad", "CALI")]), HttpStatusCode.OK);

        JsonNode.Parse(PeticionesANexo().Single().Cuerpo!)!["valores"]!.AsArray().Select(v => v!["valor"]!.GetValue<string>())
            .Should().Equal("Cali", "CALI");
        json.GetProperty("valores").GetArrayLength().Should().Be(2);
    }

    [Theory]
    [InlineData(clsBaseDatosPruebas.RolExternoGerente)]
    [InlineData(clsBaseDatosPruebas.RolExternoDirector)]
    public async Task GerenteYDirectorTambienPuedenNormalizar_PoliticaComercial(int rol)
    {
        var json = await PostAsync(Cuerpo(null, [("linea_negocio", "Nacional")]), HttpStatusCode.OK, rol);

        json.GetProperty("valores")[0].GetProperty("origen").GetString().Should().Be("linea_cotifly");
    }

    [Fact]
    public async Task LasPeticionesConcurrentesNoSeMezclan()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var tareas = Enumerable.Range(0, 8).Select(async i =>
        {
            using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(null, [("ciudad", $"Ciudad {i}"), ("tipo_vehiculo", $"V{i}")]));
            var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.OK);
            return (i, Valores: json.GetProperty("valores").EnumerateArray().Select(v => v.GetProperty("valor").GetString()).ToList());
        });

        foreach (var (i, valores) in await Task.WhenAll(tareas))
        {
            valores.Should().Equal($"Ciudad {i}", $"V{i}");
        }
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
