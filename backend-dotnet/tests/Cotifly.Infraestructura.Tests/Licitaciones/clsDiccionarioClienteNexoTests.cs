using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Escritura del diccionario del cliente en el adaptador de NEXO, contra un HTTP falso: la forma exacta de <c>POST</c>, <c>PUT /{id}</c> y
/// <c>DELETE /{id}</c> (cliente por NIT normalizado, nunca <c>clienteId</c>; el <c>PUT</c> sin cliente), sin reintentos, y la traducción de
/// los errores (los códigos del diccionario y el <c>409 TERMINO_DUPLICADO</c> pasan tal cual; otro 409 o un 404 sin código, no).
/// </summary>
public sealed class clsDiccionarioClienteNexoTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly clsDatosTerminoCliente _datos = new("Tractomula", "valor", "3S3", "tipoVehiculo", null, true);

    private const string TerminoJson =
        """{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a10","clienteId":"0f6d2a1e-3c4b-4a5d-8e9f-001122334455","clienteNombre":"Cliente Ficticio","termino":"Tractomula","tipo":"valor","concepto":"3S3","contexto":"tipoVehiculo","ejemplo":null,"activo":true,"createdAt":"2026-11-01T00:00:00Z","updatedAt":null}""";

    [Fact]
    public async Task CrearMandaElNitNormalizadoYLosCamposSinClienteId()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.Created, TerminoJson);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.CrearTerminoClienteAsync(_usuarioId, "900.555.111-2", _datos, Ct);

        resultado.Valor.Termino.Should().Be("Tractomula");
        resultado.Valor.ClienteId.Should().NotBeNull();
        resultado.Valor.EsGeneral.Should().BeFalse();
        var peticion = nexo.Peticiones.Single();
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsolutePath.Should().Be(clsImportacionExcelNexo.RutaDiccionarioCliente);
        using var cuerpo = JsonDocument.Parse(peticion.Cuerpo!);
        cuerpo.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["clienteNit", "termino", "tipo", "concepto", "contexto", "activo"]);
        cuerpo.RootElement.GetProperty("clienteNit").GetString().Should().Be("900555111");
        cuerpo.RootElement.GetProperty("activo").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("", "CLIENTE_REQUERIDO")]
    [InlineData("-", "CLIENTE_NIT_INVALIDO")]
    public async Task CrearSinUnNitValidoNoLlamaANexo(string nit, string codigo)
    {
        var nexo = new clsNexoClientesFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.CrearTerminoClienteAsync(_usuarioId, nit, _datos, Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task EditarMandaSoloLosCamposEditablesAlIdDelTermino()
    {
        var id = Guid.NewGuid();
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, TerminoJson);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.EditarTerminoClienteAsync(_usuarioId, id, _datos with { Ejemplo = "TRACTO", Activo = false }, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var peticion = nexo.Peticiones.Single();
        peticion.Metodo.Should().Be(HttpMethod.Put);
        peticion.Url.AbsolutePath.Should().Be($"{clsImportacionExcelNexo.RutaDiccionarioCliente}/{id:D}");
        using var cuerpo = JsonDocument.Parse(peticion.Cuerpo!);
        cuerpo.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["termino", "tipo", "concepto", "contexto", "ejemplo", "activo"]);
        cuerpo.RootElement.GetProperty("activo").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task BorrarLlamaAlIdDelTermino()
    {
        var id = Guid.NewGuid();
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.NoContent);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.EliminarTerminoClienteAsync(_usuarioId, id, Ct);

        resultado.Valor.Should().BeTrue();
        nexo.Peticiones.Single().Url.AbsolutePath.Should().Be($"{clsImportacionExcelNexo.RutaDiccionarioCliente}/{id:D}");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "TERMINO_REQUERIDO")]
    [InlineData(HttpStatusCode.BadRequest, "CONCEPTO_REQUERIDO")]
    [InlineData(HttpStatusCode.BadRequest, "CAMPO_DESTINO_INVALIDO")]
    [InlineData(HttpStatusCode.BadRequest, "UNIDAD_INVALIDA")]
    [InlineData(HttpStatusCode.BadRequest, "CONTEXTO_INVALIDO")]
    [InlineData(HttpStatusCode.BadRequest, "CONTEXTO_NO_APLICA")]
    [InlineData(HttpStatusCode.BadRequest, "EJEMPLO_MUY_LARGO")]
    [InlineData(HttpStatusCode.BadRequest, "CLIENTE_NIT_INVALIDO")]
    [InlineData(HttpStatusCode.BadRequest, "CLIENTE_NO_ENCONTRADO")]
    [InlineData(HttpStatusCode.Conflict, "TERMINO_DUPLICADO")]
    [InlineData(HttpStatusCode.NotFound, "TERMINO_NO_ENCONTRADO")]
    public async Task LosErroresDelDiccionarioDeNexoPasanTalCual(HttpStatusCode estado, string codigo)
    {
        var nexo = new clsNexoClientesFalso().Responder(estado, $$"""{"code":"{{codigo}}","message":"Mensaje de NEXO."}""");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.EditarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), _datos, Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        resultado.Error.Mensaje.Should().Be("Mensaje de NEXO.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"OTRO_CONFLICTO","message":"x"}""")]
    [InlineData(HttpStatusCode.Conflict, "")]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"CLIENTE_AMBIGUO","message":"x"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"TERMINO_DUPLICADO","message":"x"}""")]
    public async Task OtroConflictoUnNoEncontradoSinCodigoOUnCodigoFueraDeSuEstadoSonCosteoNexoRechazada(HttpStatusCode estado, string cuerpo)
    {
        var nexo = new clsNexoClientesFalso().Responder(estado, cuerpo);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.EliminarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
    }

    [Fact]
    public async Task LaEscrituraNoSeReintentaAnteUnFalloTransitorio()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.Created, TerminoJson);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.CrearTerminoClienteAsync(_usuarioId, "900555111", _datos, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().ContainSingle("crear un término no es idempotente: nunca se reintenta");
    }

    [Fact]
    public async Task ConNitElGetDelDiccionarioMandaElNitNormalizadoYPideLosGenerales()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, "[" + TerminoJson + "]");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900.555.111-2", new clsFiltroDiccionarioCliente(), Ct);

        resultado.Valor.Should().ContainSingle().Which.ClienteIdLeido.Should().BeTrue();
        var consulta = System.Web.HttpUtility.ParseQueryString(nexo.Peticiones.Single().Url.Query);
        consulta["clienteNit"].Should().Be("900555111");
        consulta["incluirGenerales"].Should().Be("true");
    }

    [Fact]
    public async Task ConLaImportacionApagadaLaEscrituraNoLlamaANexo()
    {
        var apagada = new clsImportacionExcelNoDisponible();

        (await apagada.CrearTerminoClienteAsync(_usuarioId, "900555111", _datos, Ct)).Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        (await apagada.EditarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), _datos, Ct)).Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        (await apagada.EliminarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), Ct)).Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    private static IClsImportacionExcelNexo Importacion(HttpMessageHandler nexo, out ServiceProvider proveedor)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }).Build());
        servicios.AddHttpClient(clsImportacionExcelNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);
        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("dicc", "Comercial Ficticio", "dicc@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1011).Valor);
        servicios.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
        proveedor = servicios.BuildServiceProvider();
        return proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>();
    }
}
