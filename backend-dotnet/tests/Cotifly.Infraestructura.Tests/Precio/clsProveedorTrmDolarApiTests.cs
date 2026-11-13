using System.Globalization;
using System.Net;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Precio;

/// <summary>
/// <see cref="clsProveedorTrmDolarApi"/> (ADR-014) contra el DolarApi falso (<see cref="clsDolarApiFalso"/>): nunca sale a la red.
/// Aquí el cliente no lleva la tubería de resiliencia (se prueba en <c>clsRegistroTrmTests</c>). Fechas de negocio en America/Bogota.
/// </summary>
public sealed class clsProveedorTrmDolarApiTests : IDisposable
{
    private static readonly DateOnly _lunes = new(2026, 10, 5);
    private readonly clsDolarApiFalso _fuente = new();
    private readonly clsMemoriaTrm _memoria = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LeeLaTrmDeDolarApiConSuFuenteYVigenteSoloElDiaPedido()
    {
        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Detalle : string.Empty);
        resultado.Valor.Valor.Should().Be(3273.49m);
        resultado.Valor.Fuente.Should().Be("dolarapi");
        resultado.Valor.VigenciaDesde.Should().Be(_lunes);
        resultado.Valor.VigenciaHasta.Should().Be(_lunes);
        _fuente.Peticiones.Should().ContainSingle().Which.AbsoluteUri.Should().Be(clsDolarApiFalso.Url);
    }

    [Fact]
    public async Task AceptaElCuerpoRealDeDolarApi()
    {
        _fuente.Responder(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T16:30:00.074Z"}""");

        (await Proveedor().ObtenerAsync(_lunes, Ct)).Valor.Valor.Should().Be(3273.49m);
    }

    [Fact]
    public async Task AceptaElValorComoTexto()
    {
        _fuente.Responder(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":"3273.49","fechaActualizacion":"2026-10-05T13:01:18.476Z"}""");

        (await Proveedor().ObtenerAsync(_lunes, Ct)).Valor.Valor.Should().Be(3273.49m);
    }

    // Misma escala que Cotizaciones.trmAplicada (DECIMAL(18,4)) y el mismo redondeo que la Superintendencia: half-to-even.
    [Theory]
    [InlineData("3273.491249", "3273.4912")]
    [InlineData("3273.49125", "3273.4912")]
    [InlineData("3273.49135", "3273.4914")]
    public async Task LaTrmSeRedondeaACuatroDecimalesHalfToEven(string valor, string esperado)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","nombre":"TRM","valor":{{valor}},"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""");

        (await Proveedor().ObtenerAsync(_lunes, Ct)).Valor.Valor.Should().Be(decimal.Parse(esperado, CultureInfo.InvariantCulture));
    }

    // Rango creíble [500, 20000], inclusivo.
    [Theory]
    [InlineData("500", true)]
    [InlineData("20000", true)]
    [InlineData("499.9999", false)]
    [InlineData("20000.0001", false)]
    [InlineData("0", false)]
    [InlineData("-3273.49", false)]
    public async Task ElValorDebeEstarEnElRangoCreible(string valor, bool aceptado)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","nombre":"TRM","valor":{{valor}},"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""");

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsExitoso.Should().Be(aceptado);
        if (!aceptado)
        {
            resultado.Error.Detalle.Should().Contain("fuera de rango");
        }
    }

    // ADR-014 §1: fechaActualizacion es la hora del refresco de DolarApi, no la vigencia. Solo vale para la
    // fecha pedida si DolarApi refrescó ese día en Bogotá antes de las 12:00; pasado el corte puede traer ya la TRM de mañana.
    [Theory]
    [InlineData("2026-10-05T05:00:00Z", true)]    // 00:00 del lunes en Bogotá
    [InlineData("2026-10-05T16:59:59Z", true)]    // 11:59:59 en Bogotá
    [InlineData("2026-10-05T17:00:00Z", false)]   // 12:00 en Bogotá: corte
    [InlineData("2026-10-05T20:02:00.074Z", false)] // 15:02 en Bogotá (como una respuesta real): puede ser la de mañana
    [InlineData("2026-10-05T04:59:59Z", false)]   // 23:59 del domingo en Bogotá: otro día
    [InlineData("2026-10-02T15:00:00Z", false)]   // viernes: puede ser otra vigencia
    [InlineData("2026-10-06T13:00:00Z", false)]   // martes: futura
    [InlineData("2026-10-05T09:00:00-05:00", true)]
    public async Task SoloSeAceptaSiSeRefrescoElDiaPedidoAntesDelCorte(string fecha, bool aceptada)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"{{fecha}}"}""");

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsExitoso.Should().Be(aceptada, resultado.EsFallo ? resultado.Error.Detalle : "aceptada");
        if (!aceptada)
        {
            resultado.Error.Detalle.Should().Contain("ventana segura");
        }
    }

    // Desviación de ±10 % frente a la última TRM válida conocida (en caché o del respaldo).
    [Theory]
    [InlineData("3000", "3273.49", true)]     // -8,35 %
    [InlineData("3000", "3300.00", true)]     // 10 % exacto
    [InlineData("3000", "3300.01", false)]
    [InlineData("3000", "2699.99", false)]
    [InlineData(null, "19000", true)]         // sin referencia no se compara
    public async Task UnValorQueSeDesviaMasDelDiezPorCientoSeRechaza(string? anterior, string valor, bool aceptado)
    {
        if (anterior is not null)
        {
            _memoria.Registrar(new Cotifly.Aplicacion.Precio.DTOs.clsTrm(decimal.Parse(anterior, CultureInfo.InvariantCulture), _lunes.AddDays(-1), _lunes.AddDays(-1), "superfinanciera"));
        }

        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","nombre":"TRM","valor":{{valor}},"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""");

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsExitoso.Should().Be(aceptado, resultado.EsFallo ? resultado.Error.Detalle : "aceptado");
        if (!aceptado)
        {
            resultado.Error.Detalle.Should().Contain("10 %");
        }
    }

    [Fact]
    public async Task UnCuerpoDeMasDeDieciseisKbSeRechaza()
    {
        var relleno = new string('x', clsProveedorTrmDolarApi.TamanoMaximoCuerpo);
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T13:01:18.476Z","x":"{{relleno}}"}""");

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Detalle.Should().Be("respuesta demasiado grande");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "HTTP 500")]
    [InlineData(HttpStatusCode.NotFound, "{}", "HTTP 404")]
    [InlineData(HttpStatusCode.OK, "no es json", "JSON inválido")]
    [InlineData(HttpStatusCode.OK, "[]", "no es un objeto")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"USD","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""", "unidad")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"Dólar blue","valor":3273.49,"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""", "nombre")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","valor":3273.49,"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""", "nombre")]
    [InlineData(HttpStatusCode.OK, """{"valor":3273.49,"fechaActualizacion":"2026-10-05T13:01:18.476Z"}""", "unidad")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","fechaActualizacion":"2026-10-05T13:01:18.476Z"}""", "sin valor")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":"abc","fechaActualizacion":"2026-10-05T13:01:18.476Z"}""", "sin valor")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":3273.49}""", "fechaActualizacion")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"ayer"}""", "fechaActualizacion")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":20261005}""", "fechaActualizacion")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-09-20T19:01:18.476Z"}""", "ventana segura")]
    public async Task UnaRespuestaInvalidaEsTrmNoDisponible(HttpStatusCode estado, string cuerpo, string motivo)
    {
        _fuente.Responder(estado, cuerpo);

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Mensaje.Should().Contain("TRM").And.Contain("pesos");
        resultado.Error.Detalle.Should().Contain(motivo);
    }

    [Fact]
    public async Task UnFalloDeRedEsTrmNoDisponibleYDejaUnaSolaLineaDeWarning()
    {
        _fuente.Fallar(new HttpRequestException("conexión rechazada"));
        var logger = new clsLoggerFalso<clsProveedorTrmDolarApi>();

        var resultado = await Proveedor(logger).ObtenerAsync(_lunes, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Detalle.Should().Be("error de red");
        logger.Registros.Where(e => e.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning).Should().ContainSingle()
            .Which.Mensaje.Should().Contain("DolarApi").And.Contain("error de red").And.Contain("2026-10-05");
    }

    [Fact]
    public async Task UnTiempoAgotadoEsTrmNoDisponible()
    {
        _fuente.Fallar(new TaskCanceledException("timeout"));

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.Error.Detalle.Should().Be("tiempo agotado");
    }

    [Fact]
    public async Task LaCancelacionDeQuienLlamaSePropaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var accion = () => Proveedor().ObtenerAsync(_lunes, cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose() => _fuente.Dispose();

    private clsProveedorTrmDolarApi Proveedor(clsLoggerFalso<clsProveedorTrmDolarApi>? logger = null) => new(
        new clsFabricaHttpFalsa(_fuente),
        Options.Create(new clsOpcionesTrm { UrlDolarApi = clsDolarApiFalso.Url }),
        new clsCalendarioNegocio(new clsRelojFalso(), Options.Create(new clsOpcionesZona())),
        _memoria,
        (Microsoft.Extensions.Logging.ILogger<clsProveedorTrmDolarApi>?)logger ?? NullLogger<clsProveedorTrmDolarApi>.Instance);

    /// <summary>Fábrica que siempre entrega un cliente sobre el manejador falso (sin tubería de resiliencia).</summary>
    private sealed class clsFabricaHttpFalsa(HttpMessageHandler manejador) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(manejador, disposeHandler: false);
    }
}
