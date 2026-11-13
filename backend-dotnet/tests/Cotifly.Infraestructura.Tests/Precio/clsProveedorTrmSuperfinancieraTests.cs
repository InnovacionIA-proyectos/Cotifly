using System.Net;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Precio;

/// <summary>
/// <see cref="clsProveedorTrmSuperfinanciera"/> (ADR-014) contra los datos abiertos falsos (<see cref="clsTrmFalso"/>): nunca sale
/// a la red. Aquí el cliente no lleva la tubería de resiliencia (se prueba en <c>clsRegistroTrmTests</c>).
/// </summary>
public sealed class clsProveedorTrmSuperfinancieraTests : IDisposable
{
    private readonly clsTrmFalso _fuente = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConsultaLaFilaQueCubreLaFechaConLaConsultaSoqlExacta()
    {
        var resultado = await Proveedor().ObtenerAsync(new DateOnly(2026, 9, 29), Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Valor.Should().Be(3312.50m);
        resultado.Valor.VigenciaDesde.Should().Be(new DateOnly(2026, 9, 29));
        resultado.Valor.Fuente.Should().Be(clsProveedorTrmSuperfinanciera.Fuente);

        var url = _fuente.Peticiones.Should().ContainSingle().Subject;
        url.AbsoluteUri.Should().Be(
            clsTrmFalso.Url + "?$select=valor%2Cvigenciadesde%2Cvigenciahasta"
            + "&$where=vigenciadesde%20%3C%3D%20%272026-09-29T00%3A00%3A00%27%20AND%20vigenciahasta%20%3E%3D%20%272026-09-29T00%3A00%3A00%27"
            + "&$order=vigenciadesde%20DESC&$limit=1");
        clsTrmFalso.Parametro(url, "$where").Should().Be("vigenciadesde <= '2026-09-29T00:00:00' AND vigenciahasta >= '2026-09-29T00:00:00'");
        url.Query.Should().NotContain("'").And.NotContain(" ");
    }

    [Theory]
    [InlineData(2026, 9, 26)] // sábado
    [InlineData(2026, 9, 27)] // domingo
    [InlineData(2026, 9, 28)] // lunes (festivo o no, la TRM del viernes cubre hasta el lunes)
    public async Task UnFinDeSemanaRecibeLaTrmDelRangoQueLoCubre(int anio, int mes, int dia)
    {
        var resultado = await Proveedor().ObtenerAsync(new DateOnly(anio, mes, dia), Ct);

        resultado.Valor.Valor.Should().Be(3306.86m);
        resultado.Valor.VigenciaDesde.Should().Be(new DateOnly(2026, 9, 26));
        resultado.Valor.VigenciaHasta.Should().Be(new DateOnly(2026, 9, 28));
    }

    // La fecha es de calendario: con zona horaria o sin ella, vale el día escrito (no se convierte a UTC ni a la hora local).
    [Theory]
    [InlineData("2026-09-26T00:00:00.000", "2026-09-28T00:00:00.000")]
    [InlineData("2026-09-26T00:00:00.000-05:00", "2026-09-28T23:00:00.000+14:00")]
    [InlineData("2026-09-26", "2026-09-28")]
    public async Task LasFechasDeVigenciaSeLeenSinConvertirZonaHoraria(string desde, string hasta)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""[{"valor":"3306.86","vigenciadesde":"{{desde}}","vigenciahasta":"{{hasta}}"}]""");

        var trm = (await Proveedor().ObtenerAsync(new DateOnly(2026, 9, 28), Ct)).Valor;

        trm.VigenciaDesde.Should().Be(new DateOnly(2026, 9, 26));
        trm.VigenciaHasta.Should().Be(new DateOnly(2026, 9, 28));
    }

    [Fact]
    public async Task AceptaElValorComoNumeroJson()
    {
        _fuente.Responder(HttpStatusCode.OK, """[{"valor":3306.8612,"vigenciadesde":"2026-09-26T00:00:00.000","vigenciahasta":"2026-09-28T00:00:00.000"}]""");

        (await Proveedor().ObtenerAsync(new DateOnly(2026, 9, 27), Ct)).Valor.Valor.Should().Be(3306.8612m);
    }

    // La TRM se redondea a 4 decimales (escala de Cotizaciones.trmAplicada, DECIMAL(18,4)): se guarda la misma que convierte.
    [Theory]
    [InlineData("3306.861249", "3306.8612")]
    [InlineData("3306.86125", "3306.8612")]
    [InlineData("3306.86135", "3306.8614")]
    public async Task LaTrmSeRedondeaACuatroDecimales(string valor, string esperado)
    {
        _fuente.Responder(HttpStatusCode.OK, $$"""[{"valor":"{{valor}}","vigenciadesde":"2026-09-26T00:00:00.000","vigenciahasta":"2026-09-28T00:00:00.000"}]""");

        (await Proveedor().ObtenerAsync(new DateOnly(2026, 9, 27), Ct)).Valor.Valor
            .Should().Be(decimal.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "[]", "HTTP 500")]
    [InlineData(HttpStatusCode.OK, "[]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "{\"error\":true}", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"0\",\"vigenciadesde\":\"2026-09-26T00:00:00.000\",\"vigenciahasta\":\"2026-09-28T00:00:00.000\"}]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"0.00001\",\"vigenciadesde\":\"2026-09-26T00:00:00.000\",\"vigenciahasta\":\"2026-09-28T00:00:00.000\"}]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"-1\",\"vigenciadesde\":\"2026-09-26T00:00:00.000\",\"vigenciahasta\":\"2026-09-28T00:00:00.000\"}]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"abc\",\"vigenciadesde\":\"2026-09-26T00:00:00.000\",\"vigenciahasta\":\"2026-09-28T00:00:00.000\"}]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"3306.86\",\"vigenciadesde\":\"2026-09-01T00:00:00.000\",\"vigenciahasta\":\"2026-09-02T00:00:00.000\"}]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"3306.86\"}]", "sin TRM vigente")]
    [InlineData(HttpStatusCode.OK, "no es json", "JSON inválido")]
    [InlineData(HttpStatusCode.OK, "[{\"valor\":\"3306.86\",\"vigenciadesde\":\"26/09/2026\",\"vigenciahasta\":\"2026-09-28T00:00:00.000\"}]", "sin TRM vigente")]
    public async Task UnaRespuestaInvalidaEsTrmNoDisponible(HttpStatusCode estado, string cuerpo, string motivo)
    {
        _fuente.Responder(estado, cuerpo);

        var resultado = await Proveedor().ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Mensaje.Should().Contain("TRM").And.Contain("pesos");
        resultado.Error.Detalle.Should().Contain(motivo);
    }

    [Fact]
    public async Task UnFalloDeRedEsTrmNoDisponibleYDejaUnaSolaLineaDeWarning()
    {
        _fuente.Fallar(new HttpRequestException("conexión rechazada"));
        var logger = new clsLoggerFalso<clsProveedorTrmSuperfinanciera>();

        var resultado = await Proveedor(logger).ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Detalle.Should().Be("error de red");
        logger.Registros.Where(e => e.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning).Should().ContainSingle()
            .Which.Mensaje.Should().Contain("error de red").And.Contain("2026-09-27");
    }

    [Fact]
    public async Task UnTiempoAgotadoEsTrmNoDisponible()
    {
        _fuente.Fallar(new TaskCanceledException("timeout"));

        var resultado = await Proveedor().ObtenerAsync(new DateOnly(2026, 9, 27), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        resultado.Error.Detalle.Should().Be("tiempo agotado");
    }

    [Fact]
    public async Task LaCancelacionDeQuienLlamaSePropaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var accion = () => Proveedor().ObtenerAsync(new DateOnly(2026, 9, 27), cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void LaResilienciaUsaElTiempoConfiguradoYUnTercioPorIntento()
    {
        var resiliencia = new Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions();

        clsResilienciaTrm.Configurar(resiliencia, TimeSpan.FromSeconds(60));

        resiliencia.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(60));
        resiliencia.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(20));
        resiliencia.Retry.MaxRetryAttempts.Should().Be(clsResilienciaTrm.Reintentos);
        resiliencia.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
        resiliencia.CircuitBreaker.MinimumThroughput.Should().Be(clsResilienciaTrm.UmbralCircuito, "con el valor por defecto (100) nunca abriría");
    }

    public void Dispose() => _fuente.Dispose();

    private clsProveedorTrmSuperfinanciera Proveedor(clsLoggerFalso<clsProveedorTrmSuperfinanciera>? logger = null) => new(
        new clsFabricaHttpFalsa(_fuente),
        Options.Create(new clsOpcionesTrm { Url = clsTrmFalso.Url }),
        (Microsoft.Extensions.Logging.ILogger<clsProveedorTrmSuperfinanciera>?)logger ?? NullLogger<clsProveedorTrmSuperfinanciera>.Instance);

    /// <summary>Fábrica que siempre entrega un cliente sobre el manejador falso (sin tubería de resiliencia).</summary>
    private sealed class clsFabricaHttpFalsa(HttpMessageHandler manejador) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(manejador, disposeHandler: false);
    }
}
