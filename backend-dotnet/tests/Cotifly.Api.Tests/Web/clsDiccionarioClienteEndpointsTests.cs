using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Crear, editar y borrar términos del diccionario del cliente por HTTP, contra la API real y el Nexo falso: solo el rol activo
/// <c>comercial</c> escribe (el gerente y el director consultan), el NIT es obligatorio, los términos generales no se tocan, un id de otro
/// cliente es 404 y los errores de NEXO pasan con su código. Cada respuesta se valida contra <c>specs/openapi.yaml</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsDiccionarioClienteEndpointsTests : IDisposable
{
    private const string Ruta = "/web/diccionario-cliente";
    private const string RutaId = "/web/diccionario-cliente/{id}";
    private const string RutaNexo = "/api/diccionario-cliente";
    private const string Nit = "900.555.111-2";
    private const string NitNormalizado = "900555111";

    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsDiccionarioClienteEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static string Cuerpo(
        string? nit = Nit, string? termino = "Tracto", string? tipo = "valor", string? concepto = "3S3", string? contexto = "tipoVehiculo",
        string? ejemplo = null, bool? activo = null) =>
        JsonSerializer.Serialize(new { nit_cliente = nit, termino, tipo, concepto, contexto, ejemplo, activo });

    private Task<clsSesionPrueba> ComercialAsync() => _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

    private Guid TerminoDelCliente(string termino = "Mula del cliente")
    {
        var nuevo = new clsTerminoFalso(clsNexoFalso.ClienteIdDeNit(Nit), termino, "valor", "3S2", "tipoVehiculo");
        _nexo.Diccionario.Add(nuevo);
        return nuevo.Id;
    }

    private int EscriturasEnNexo() =>
        _nexo.Peticiones.Count(p => p.Metodo != HttpMethod.Get && p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));

    // --- Crear ---

    [Fact]
    public async Task CrearResponde201ConElTerminoDelClienteYLuegoApareceEnLaConsulta()
    {
        var sesion = await ComercialAsync();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(ejemplo: "TRACTO 3S3"));

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.Created);
        json.GetProperty("termino").GetString().Should().Be("Tracto");
        json.GetProperty("general").GetBoolean().Should().BeFalse();
        json.GetProperty("activo").GetBoolean().Should().BeTrue();
        var enviado = JsonNode.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath == RutaNexo).Cuerpo!)!;
        enviado["clienteNit"]!.GetValue<string>().Should().Be(NitNormalizado);
        enviado["clienteId"].Should().BeNull();

        using var consulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={NitNormalizado}", sesion.Access);
        var lista = await VerificarContratoAsync(consulta, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        lista.GetProperty("terminos").EnumerateArray().Should().Contain(t => t.GetProperty("termino").GetString() == "Tracto"
            && !t.GetProperty("general").GetBoolean());
    }

    [Fact]
    public async Task CrearUnTerminoRepetidoEs409TerminoDuplicado()
    {
        var sesion = await ComercialAsync();
        using var primero = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo());

        using var segundo = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(termino: " TRACTO "));

        primero.StatusCode.Should().Be(HttpStatusCode.Created);
        segundo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(segundo, "TERMINO_DUPLICADO");
    }

    [Theory]
    [InlineData(null, "Tracto", "valor", "3S3", "CLIENTE_REQUERIDO")]
    [InlineData(Nit, " ", "valor", "3S3", "TERMINO_REQUERIDO")]
    [InlineData(Nit, "Tracto", "", "3S3", "TIPO_TERMINO_INVALIDO")]
    [InlineData(Nit, "Tracto", "valor", null, "CONCEPTO_REQUERIDO")]
    public async Task LosVaciosSon400ConElCodigoSinLlamarANexo(string? nit, string? termino, string? tipo, string? concepto, string codigo)
    {
        var sesion = await ComercialAsync();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(nit, termino, tipo, concepto));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, codigo);
        EscriturasEnNexo().Should().Be(0);
    }

    [Fact]
    public async Task UnCampoMasLargoDeLoPermitidoEs400ErrorWebSinLlamarANexo()
    {
        var sesion = await ComercialAsync();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(termino: new string('x', 201)));

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "los largos de más son ErrorWeb, sin código");
        EscriturasEnNexo().Should().Be(0);
    }

    [Theory]
    [InlineData("columna", "otro_campo", null, "CAMPO_DESTINO_INVALIDO")]
    [InlineData("valor", "3S3", "otroContexto", "CONTEXTO_INVALIDO")]
    [InlineData("columna", "origen", "tipoVehiculo", "CONTEXTO_NO_APLICA")]
    public async Task LosRechazosDeNexoPasanConSuCodigo(string tipo, string concepto, string? contexto, string codigo)
    {
        var sesion = await ComercialAsync();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(tipo: tipo, concepto: concepto, contexto: contexto));

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
    }

    [Theory]
    [InlineData(clsBaseDatosPruebas.RolExternoGerente)]
    [InlineData(clsBaseDatosPruebas.RolExternoDirector)]
    public async Task ElGerenteYElDirectorConsultanPeroNoEscriben(int rol)
    {
        var sesion = await _api.IniciarSesionAsync(rol);
        var id = TerminoDelCliente();

        using var consulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={NitNormalizado}", sesion.Access);
        using var opciones = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}/opciones", sesion.Access);
        using var crear = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo());
        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{id}", sesion.Access, Cuerpo());
        using var borrar = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{id}?nit_cliente={NitNormalizado}", sesion.Access);

        consulta.StatusCode.Should().Be(HttpStatusCode.OK);
        opciones.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var (respuesta, ruta, metodo) in new[] { (crear, Ruta, HttpMethod.Post), (editar, RutaId, HttpMethod.Put), (borrar, RutaId, HttpMethod.Delete) })
        {
            var json = await VerificarContratoAsync(respuesta, ruta, metodo, HttpStatusCode.Forbidden);
            json.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "el 403 por rol es ErrorWeb sin código");
        }

        EscriturasEnNexo().Should().Be(0);
    }

    [Fact]
    public async Task SinSesionLaEscrituraEs401()
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, null, Cuerpo());

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- Editar ---

    [Fact]
    public async Task EditarUnTerminoDelClienteResponde200()
    {
        var sesion = await ComercialAsync();
        var id = TerminoDelCliente();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{id}", sesion.Access, Cuerpo(termino: "Mula editada", activo: false));

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Put, HttpStatusCode.OK);
        json.GetProperty("id").GetGuid().Should().Be(id);
        json.GetProperty("termino").GetString().Should().Be("Mula editada");
        json.GetProperty("activo").GetBoolean().Should().BeFalse();
        var enviado = JsonNode.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Put).Cuerpo!)!.AsObject();
        enviado.Select(p => p.Key).Should().NotContain(["clienteNit", "clienteId"]);
    }

    [Fact]
    public async Task EditarUnTerminoGeneralEs403TerminoGeneralNoEditableSinEscribirEnNexo()
    {
        var sesion = await ComercialAsync();
        var general = _nexo.Diccionario.First(t => t.ClienteId is null).Id;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{general}", sesion.Access, Cuerpo());

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Put, HttpStatusCode.Forbidden);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("TERMINO_GENERAL_NO_EDITABLE");
        EscriturasEnNexo().Should().Be(0);
    }

    [Fact]
    public async Task EditarUnTerminoDeOtroClienteEs404SinEscribirEnNexo()
    {
        var sesion = await ComercialAsync();
        var deOtro = new clsTerminoFalso(clsNexoFalso.ClienteIdDeNit("800111222"), "Mula ajena", "valor", "3S2", "tipoVehiculo");
        _nexo.Diccionario.Add(deOtro);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{deOtro.Id}", sesion.Access, Cuerpo());

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Put, HttpStatusCode.NotFound);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("TERMINO_NO_ENCONTRADO");
        EscriturasEnNexo().Should().Be(0);
        _nexo.Diccionario.Single(t => t.Id == deOtro.Id).Termino.Should().Be("Mula ajena");
    }

    [Fact]
    public async Task EditarConUnIdQueNoEsGuidEs404SinLlamarANexo()
    {
        var sesion = await ComercialAsync();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/no-es-guid", sesion.Access, Cuerpo());

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(respuesta, "TERMINO_NO_ENCONTRADO");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));
    }

    // --- Borrar ---

    [Fact]
    public async Task BorrarUnTerminoDelClienteResponde204()
    {
        var sesion = await ComercialAsync();
        var id = TerminoDelCliente();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{id}?nit_cliente={Uri.EscapeDataString(Nit)}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _nexo.Diccionario.Should().NotContain(t => t.Id == id);
    }

    [Fact]
    public async Task BorrarSinNitEs400ClienteRequeridoSinLlamarANexo()
    {
        var sesion = await ComercialAsync();
        var id = TerminoDelCliente();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{id}", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Delete, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CLIENTE_REQUERIDO");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BorrarUnTerminoGeneralEs403()
    {
        var sesion = await ComercialAsync();
        var general = _nexo.Diccionario.First(t => t.ClienteId is null).Id;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{general}?nit_cliente={NitNormalizado}", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Delete, HttpStatusCode.Forbidden);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("TERMINO_GENERAL_NO_EDITABLE");
        _nexo.Diccionario.Should().Contain(t => t.Id == general);
    }

    [Fact]
    public async Task SiNexoNoFiltraPorNitNoSeBorraNingunTerminoDeCliente()
    {
        var sesion = await ComercialAsync();
        var id = TerminoDelCliente();
        _nexo.Diccionario.Add((Guid.NewGuid(), "Mula ajena", "valor", "3S2", "tipoVehiculo"));
        _nexo.DiccionarioIgnoraNit = true;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{id}?nit_cliente={NitNormalizado}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(respuesta, "TERMINO_NO_ENCONTRADO");
        _nexo.Diccionario.Should().Contain(t => t.Id == id);
    }

    [Fact]
    public async Task ConNexoCaidoLaEscrituraEs503()
    {
        var sesion = await ComercialAsync();
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo());

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.ServiceUnavailable);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
