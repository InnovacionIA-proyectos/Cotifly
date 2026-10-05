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
/// <see cref="clsProveedorTrmDolarApi"/> (HU-012.1, ADR-014) contra el DolarApi falso (<see cref="clsDolarApiFalso"/>): nunca sale a la red.
/// Aquí el cliente no lleva la tubería de resiliencia (se prueba en <c>clsRegistroTrmTests</c>). Fechas de negocio en America/Bogota.
/// </summary>
public sealed class clsProveedorTrmDolarApiTests : IDisposable
{
    private static readonly DateOnly _lunes = new(2026, 10, 5);
    private readonly clsDolarApiFalso _fuente = new();

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
        _fuente.Responder(HttpStatusCode.OK, """{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T19:01:18.476Z"}""");

        (await Proveedor().ObtenerAsync(_lunes, Ct)).Valor.Valor.Should().Be(3273.49m);
    }

    [Fact]
    public async Task AceptaElValorComoTexto()
    {
        _fuente.Responder(HttpStatusCode.OK, """{"unidad":"COP","valor":"3273.49","fechaActualizacion":"2026-10-05T19:01:18.476Z"}""");

        (await Proveedor().ObtenerAsync(_lunes, Ct)).Valor.Valor.Should().Be(3273.49m);
    }

    // Misma escala que Cotizaciones.trmAplicada (DECIMAL(18,4)) y el mismo redondeo que la Superintendencia: half-to-even.
    [Theory]
    [InlineData("3273.491249", "3273.4912")]
    [InlineData("3273.49125", "3273.4912")]
    [InlineData("3273.49135", "3273.4914")]
    public async Task LaTrmSeRedondeaACuatroDecimalesHalfToEven(string valor, string esperado)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","valor":{{valor}},"fechaActualizacion":"2026-10-05T19:01:18.476Z"}""");

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
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","valor":{{valor}},"fechaActualizacion":"2026-10-05T19:01:18.476Z"}""");

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsExitoso.Should().Be(aceptado);
        if (!aceptado)
        {
            resultado.Error.Detalle.Should().Contain("fuera de rango");
        }
    }

    // fechaActualizacion se lleva a la fecha de Bogotá: hasta 4 días antes de la pedida y hasta 1 después (publicación de la tarde).
    [Theory]
    [InlineData("2026-10-01T15:00:00Z", true)]    // jueves 1, 4 días antes
    [InlineData("2026-10-01T04:59:59Z", false)]   // 23:59 del miércoles 30 en Bogotá: 5 días antes
    [InlineData("2026-10-06T04:59:59Z", true)]    // 23:59 del lunes 5 en Bogotá
    [InlineData("2026-10-06T21:00:00Z", true)]    // martes 6: la del día siguiente
    [InlineData("2026-10-07T05:00:00Z", false)]   // 00:00 del miércoles 7 en Bogotá: futura
    [InlineData("2027-01-01T00:00:00Z", false)]
    [InlineData("2026-10-05T14:01:18-05:00", true)]
    public async Task LaFechaDePublicacionDebeSerRecienteYNoFutura(string fecha, bool aceptada)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""{"unidad":"COP","valor":3273.49,"fechaActualizacion":"{{fecha}}"}""");

        var resultado = await Proveedor().ObtenerAsync(_lunes, Ct);

        resultado.EsExitoso.Should().Be(aceptada, resultado.EsFallo ? resultado.Error.Detalle : "aceptada");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "HTTP 500")]
    [InlineData(HttpStatusCode.NotFound, "{}", "HTTP 404")]
    [InlineData(HttpStatusCode.OK, "no es json", "JSON inválido")]
    [InlineData(HttpStatusCode.OK, "[]", "no es un objeto")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"USD","valor":3273.49,"fechaActualizacion":"2026-10-05T19:01:18.476Z"}""", "unidad")]
    [InlineData(HttpStatusCode.OK, """{"valor":3273.49,"fechaActualizacion":"2026-10-05T19:01:18.476Z"}""", "unidad")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","fechaActualizacion":"2026-10-05T19:01:18.476Z"}""", "sin valor")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","valor":"abc","fechaActualizacion":"2026-10-05T19:01:18.476Z"}""", "sin valor")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","valor":3273.49}""", "fechaActualizacion")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","valor":3273.49,"fechaActualizacion":"ayer"}""", "fechaActualizacion")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","valor":3273.49,"fechaActualizacion":20261005}""", "fechaActualizacion")]
    [InlineData(HttpStatusCode.OK, """{"unidad":"COP","valor":3273.49,"fechaActualizacion":"2026-09-20T19:01:18.476Z"}""", "desactualizada")]
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
        (Microsoft.Extensions.Logging.ILogger<clsProveedorTrmDolarApi>?)logger ?? NullLogger<clsProveedorTrmDolarApi>.Instance);

    /// <summary>Fábrica que siempre entrega un cliente sobre el manejador falso (sin tubería de resiliencia).</summary>
    private sealed class clsFabricaHttpFalsa(HttpMessageHandler manejador) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(manejador, disposeHandler: false);
    }
}
