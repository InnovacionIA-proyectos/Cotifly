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
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Pruebas adversariales del adaptador del diccionario: ninguna escritura se reintenta (ni editar ni borrar), cada código de §3.19.1 solo
/// pasa con su estado, NEXO sin permiso o caído se traduce sin perder el código, y ni el NIT ni el texto del término llegan a los logs.
/// </summary>
public sealed class clsDiccionarioClienteNexoAdversarialTests
{
    private const string Nit = "900.555.111-2";
    private const string NitNormalizado = "900555111";
    private const string TextoSensible = "Tractomula de Cliente Secreto";
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private static readonly clsDatosTerminoCliente _datos = new(TextoSensible, "valor", "3S3", "tipoVehiculo", "ejemplo con PII", true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NiEditarNiBorrarSeReintentanAnteUnFalloTransitorio(HttpStatusCode estado)
    {
        var nexoEditar = new clsNexoClientesFalso().Responder(estado).Responder(HttpStatusCode.OK, "{}");
        var nexoBorrar = new clsNexoClientesFalso().Responder(estado).Responder(HttpStatusCode.NoContent);
        var editar = Importacion(nexoEditar, out var p1, out _);
        var borrar = Importacion(nexoBorrar, out var p2, out _);
        using var l1 = p1;
        using var l2 = p2;

        var editado = await editar.EditarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), _datos, Ct);
        var borrado = await borrar.EliminarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), Ct);

        editado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        borrado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexoEditar.Peticiones.Should().ContainSingle("editar no se reintenta");
        nexoBorrar.Peticiones.Should().ContainSingle("borrar no se reintenta");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task NexoSinPermisoEnLaEscrituraEsNexoSinPermiso(HttpStatusCode estado)
    {
        var nexo = new clsNexoClientesFalso().Responder(estado, """{"code":"FORBIDDEN","message":"x"}""");
        var importacion = Importacion(nexo, out var proveedor, out _);
        using var limpieza = proveedor;

        var resultado = await importacion.CrearTerminoClienteAsync(_usuarioId, Nit, _datos, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoSinPermiso);
        nexo.Peticiones.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "TERMINO_NO_ENCONTRADO", "TERMINO_NO_ENCONTRADO")]
    [InlineData(HttpStatusCode.Conflict, "TERMINO_NO_ENCONTRADO", "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.NotFound, "TERMINO_DUPLICADO", "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA", "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.Conflict, "CONCURRENCIA", "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "TERMINO_REQUERIDO", "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION_ERROR", "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.BadRequest, "CLIENTE_AMBIGUO", "COSTEO_NEXO_RECHAZADA")]
    public async Task AlBorrarCadaCodigoSoloPasaConSuEstado(HttpStatusCode estado, string codigoNexo, string esperado)
    {
        var nexo = new clsNexoClientesFalso().Responder(estado, $$"""{"code":"{{codigoNexo}}","message":"m"}""");
        var importacion = Importacion(nexo, out var proveedor, out _);
        using var limpieza = proveedor;

        var resultado = await importacion.EliminarTerminoClienteAsync(_usuarioId, Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(esperado);
    }

    [Fact]
    public async Task UnDuplicadoAlCrearLlegaConElMensajeDeNexo()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.Conflict, """{"code":"TERMINO_DUPLICADO","message":"Ya existe."}""");
        var importacion = Importacion(nexo, out var proveedor, out _);
        using var limpieza = proveedor;

        var resultado = await importacion.CrearTerminoClienteAsync(_usuarioId, Nit, _datos, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoDuplicado);
        resultado.Error.Mensaje.Should().Be("Ya existe.");
    }

    [Fact]
    public async Task UnCreadoSinCuerpoOConJsonRotoEsNoDisponibleYNoSeReintenta()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.Created, "{ roto").Responder(HttpStatusCode.Created, "{}");
        var importacion = Importacion(nexo, out var proveedor, out _);
        using var limpieza = proveedor;

        var resultado = await importacion.CrearTerminoClienteAsync(_usuarioId, Nit, _datos, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().ContainSingle();
    }

    [Fact]
    public async Task UnicodeYCaracteresDeControlViajanIntactosEnElCuerpo()
    {
        const string Termino = "Ñandú 🚚 \"3S3\" <b>  fin";
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.Created,
            """{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a10","clienteId":"0f6d2a1e-3c4b-4a5d-8e9f-001122334455","clienteNombre":"C","termino":"x","tipo":"valor","concepto":"3S3","contexto":"tipoVehiculo","ejemplo":null,"activo":true,"createdAt":"2026-11-01T00:00:00Z","updatedAt":null}""");
        var importacion = Importacion(nexo, out var proveedor, out _);
        using var limpieza = proveedor;

        await importacion.CrearTerminoClienteAsync(_usuarioId, Nit, _datos with { Termino = Termino }, Ct);

        using var cuerpo = JsonDocument.Parse(nexo.Peticiones.Single().Cuerpo!);
        cuerpo.RootElement.GetProperty("termino").GetString().Should().Be(Termino);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"CLIENTE_NIT_INVALIDO","message":"El NIT '900555111' de Tractomula de Cliente Secreto no existe."}""")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"TERMINO_DUPLICADO","message":"Tractomula de Cliente Secreto ya existe para 900555111."}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"message":"Tractomula de Cliente Secreto 900555111"}""")]
    public async Task NiElNitNiElTextoDelTerminoLleganALosLogs(HttpStatusCode estado, string cuerpo)
    {
        var nexoCrear = new clsNexoClientesFalso().Responder(estado, cuerpo);
        var nexoListar = new clsNexoClientesFalso().Responder(estado, cuerpo);
        var crear = Importacion(nexoCrear, out var p1, out var logsCrear);
        var listar = Importacion(nexoListar, out var p2, out var logsListar);
        using var l1 = p1;
        using var l2 = p2;

        await crear.CrearTerminoClienteAsync(_usuarioId, Nit, _datos, Ct);
        await listar.ObtenerDiccionarioClienteAsync(_usuarioId, Nit, new clsFiltroDiccionarioCliente(Q: TextoSensible), Ct);

        var mensajes = logsCrear.Registros.Concat(logsListar.Registros)
            .Select(r => r.Registro.Mensaje + " " + r.Registro.Excepcion).ToList();
        mensajes.Should().NotBeEmpty();
        mensajes.Should().NotContain(m => m.Contains(NitNormalizado, StringComparison.Ordinal), "el NIT no va a los logs");
        mensajes.Should().NotContain(m => m.Contains("Secreto", StringComparison.Ordinal), "el texto del término no va a los logs");
    }

    private static IClsImportacionExcelNexo Importacion(HttpMessageHandler nexo, out ServiceProvider proveedor, out clsProveedorLogsFalso logs)
    {
        var capturados = new clsProveedorLogsFalso();
        logs = capturados;
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capturados));
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
