using System.Net;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
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
/// Validación adversarial de HU-044.5 (#256) con respuestas raras de NEXO (un <see cref="clsNexoClientesFalso"/> que responde el cuerpo
/// que se le pida) a través del adaptador real y del servicio de formatos del cliente: el filtro del diccionario, la receta opaca en la
/// respuesta, cuerpos vacíos o rotos y los 404 sin el código esperado.
/// </summary>
public sealed class clsFormatosClienteNexoAdversarialTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TerminoGeneral =
        """{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a01","clienteId":null,"clienteNombre":null,"termino":"Tractomula","tipo":"valor","concepto":"3S3","contexto":"tipoVehiculo","ejemplo":null,"activo":true,"createdAt":"2026-11-01T00:00:00Z","updatedAt":null}""";

    [Fact]
    public async Task UnTerminoDeClienteConUnClienteIdMalFormadoNoSaleNiRompeEnUn500()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK,
            "[" + TerminoGeneral + """,{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a02","clienteId":"ACME","termino":"Mula ACME","tipo":"valor","concepto":"3S2","activo":true,"createdAt":"2026-11-01T00:00:00Z"}]""");
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.ConsultarDiccionarioAsync(_usuarioId, "900555111", null, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible, "una respuesta que no se puede leer se trata como NEXO no disponible");
    }

    [Fact]
    public async Task UnTerminoSinElCampoClienteIdPeroConClienteNombreNoSePresentaComoGeneral()
    {
        // NEXO cambia el nombre del campo (clienteNit en vez de clienteId, o lo omite): el filtro de Cotifly solo mira clienteId == null.
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK,
            "[" + TerminoGeneral + """,{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a03","clienteNit":"800111222","clienteNombre":"Cliente Ficticio ACME","termino":"Mula ACME","tipo":"valor","concepto":"3S2","activo":true,"createdAt":"2026-11-01T00:00:00Z"}]""");
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.ConsultarDiccionarioAsync(_usuarioId, null, null, null, Ct);

        resultado.Valor.Terminos.Select(t => t.Termino).Should().Equal(["Tractomula"],
            "un término que NEXO marca con un cliente (aunque sea solo por el nombre) no debe salir como general");
    }

    [Fact]
    public async Task UnaListaConNulosDelDiccionarioNoRompe()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, "[null," + TerminoGeneral + ",null]");
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.ConsultarDiccionarioAsync(_usuarioId, null, null, null, Ct);

        resultado.Valor.Terminos.Should().ContainSingle(t => t.Termino == "Tractomula");
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, "")]
    [InlineData(HttpStatusCode.Created, "null")]
    [InlineData(HttpStatusCode.OK, "<html>proxy</html>")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task UnPutDePlantillaConUnCuerpoRaroEsNexoNoDisponible(HttpStatusCode estado, string cuerpo)
    {
        var nexo = new clsNexoClientesFalso().Responder(estado, cuerpo);
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.GuardarPlantillaAsync(_usuarioId,
            new clsPeticionPlantillaMapeo("900555111", null, ["ORIGEN"], [new(0, "origen", null)]), Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().ContainSingle("guardar la plantilla no se reintenta");
    }

    [Fact]
    public async Task ElPrimerPutQueNexoRespondeConUn200NoSeInformaComoCreado()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK,
            """{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a04","clienteId":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a05","clienteNombre":"Cliente","nombre":null,"encabezados":["ORIGEN"],"columnas":[{"indice":0,"campo":"origen","transformacion":"ninguna"}],"createdAt":"2026-11-01T00:00:00Z","updatedAt":null}""");
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.GuardarPlantillaAsync(_usuarioId,
            new clsPeticionPlantillaMapeo("900555111", null, ["ORIGEN"], [new(0, "origen", null)]), Ct);

        resultado.Valor.Creada.Should().BeFalse("201 o 200 lo decide NEXO; Cotifly solo lo refleja");
    }

    [Fact]
    public async Task LaRecetaQueDevuelveNexoSeConservaTalCual()
    {
        const string receta = """{ "version":1, "hojas":[{"hoja":"*","filasEncabezado":[3]}], "precision": 12345678901234567890.123456789, "Clave_Rara":{"a":[1,2.50,-0e0]} }""";
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK,
            """[{"id":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a06","clienteId":"5d1c7a52-8b51-4c8f-9e1b-1f8f6f6c0a07","clienteNombre":"Cliente","nombre":"F","receta":"""
            + receta + ""","instrucciones":null,"usos":0,"ultimoUsoAt":null,"createdAt":"2026-11-01T00:00:00Z","updatedAt":null}]""");
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.ListarRecetasAsync(_usuarioId, "900555111", Ct);

        resultado.Valor.Single().Receta.GetRawText().Should().Be(receta, "la receta es opaca: ni se tipa ni se reescribe");
    }

    [Theory]
    [InlineData("""{"code":"RUTA_NO_ENCONTRADA","message":"DELETE /api/captura-solicitudes/recetas-lectura/x"}""")]
    [InlineData("")]
    public async Task UnDeleteQueNexoNoConoceSinElCodigoEsperadoEsRechazadaNoNoEncontrada(string cuerpo)
    {
        // Caracterización: si NEXO aún no tiene la ruta (PR #433) o responde 404 sin código, el front recibe 400 COSTEO_NEXO_RECHAZADA.
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.NotFound, cuerpo);
        var servicio = Servicio(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await servicio.EliminarRecetaAsync(_usuarioId, Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
        resultado.Error.Mensaje.Should().NotContain("recetas-lectura", "el mensaje no expone la ruta interna de NEXO");
    }

    [Fact]
    public async Task UnMensajeDeNexoConElNitNoSeCambiaPeroElLogNoLoLleva()
    {
        var logs = new clsLogsCapturados();
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.BadRequest,
            """{"code":"CLIENTE_NIT_INVALIDO","message":"El NIT '800111222' no corresponde a ningún cliente ni prospecto."}""");
        var servicio = Servicio(nexo, out var proveedor, logs);
        using var limpieza = proveedor;

        var resultado = await servicio.ListarPlantillasAsync(_usuarioId, "800111222", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
        logs.Mensajes.Should().NotContain(m => m.Contains("800111222", StringComparison.Ordinal), "el NIT del cliente no va a los logs");
    }

    private static IClsFormatosClienteService Servicio(
        HttpMessageHandler nexo, out ServiceProvider proveedor, clsLogsCapturados? logs = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging(b =>
        {
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });
        servicios.AddInfraestructura(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }).Build());
        servicios.AddHttpClient(clsImportacionExcelNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);
        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("vval", "Validador Ficticio", "vval@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1009).Valor);
        servicios.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
        proveedor = servicios.BuildServiceProvider();
        return new clsFormatosClienteService(proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>());
    }

    /// <summary>Captura el texto de cada log (para comprobar que no lleva datos del cliente).</summary>
    private sealed class clsLogsCapturados : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _mensajes = new();

        public IReadOnlyList<string> Mensajes => [.. _mensajes];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new clsLogger(_mensajes);

        public void Dispose()
        {
        }

        private sealed class clsLogger(System.Collections.Concurrent.ConcurrentQueue<string> mensajes) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                mensajes.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            }
        }
    }
}
