using System.Net;
using System.Text;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// <see cref="clsFuenteMargenOData"/>: port de <c>ODataFuente</c> (<c>mcp_server/margen/odata_fuente.py</c>) contra un SAP TM
/// falso (<see cref="clsSapTmFalso"/>, el equivalente de <c>respx</c>): nunca sale a la red. Aquí el adaptador recibe un
/// <see cref="HttpClient"/> directo, sin la tubería de resiliencia (esa se prueba en <c>clsRegistroFuenteMargenTests</c>).
/// </summary>
public sealed class clsFuenteMargenODataTests : IDisposable
{
    private const string Usuario = "svc_cotifly";
    private const string Clave = "Sup3r-Secreta-SAP";

    private static readonly object _filaOptn = new { SalesOrgId = "50000005", TrqType = "OPTN", OrderPartyId = "15927", Value = "13.5" };

    private readonly clsSapTmFalso _sap = new();
    private readonly HttpClient _http;

    public clsFuenteMargenODataTests() => _http = new HttpClient(_sap);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // test_odata_encuentra
    [Fact]
    public async Task EncuentraElValueConLaConsultaExactaDeRentabClienteSet()
    {
        _sap.ResponderFilas(_filaOptn);

        var valor = await Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        valor.Should().Be(13.5m);
        var peticion = _sap.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Get);
        peticion.Url.AbsoluteUri.Should().Be(
            clsSapTmFalso.UrlBase + "/RentabClienteSet?$filter=SalesOrgId%20eq%20%2750000005%27%20and%20TrqType%20eq%20%27OPTN%27"
            + "%20and%20OrderPartyId%20eq%20%2715927%27&$format=json");
        clsSapTmFalso.FiltroDe(peticion.Url).Should().Be("SalesOrgId eq '50000005' and TrqType eq 'OPTN' and OrderPartyId eq '15927'");
        peticion.Accept.Should().Be("application/json");
    }

    // test_odata_cliente_inyectado (el Python inyecta un httpx.Client con usuario y clave)
    [Fact]
    public async Task EnviaAutenticacionBasicConUsuarioYClave()
    {
        _sap.ResponderFilas(_filaOptn);

        (await Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct)).Should().Be(13.5m);

        var esperado = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Usuario}:{Clave}"));
        _sap.Peticiones.Should().ContainSingle().Which.Autorizacion.Should().Be(esperado);
    }

    [Fact]
    public async Task ConSapClientAgregaElMandanteAlFinalDeLaConsulta()
    {
        _sap.ResponderFilas(_filaOptn);

        (await Fuente(sapClient: " 100 ").ObtenerValorAsync("50000005", "OPTN", "15927", Ct)).Should().Be(13.5m);

        _sap.Peticiones.Single().Url.AbsoluteUri.Should().Be(
            clsSapTmFalso.UrlBase + "/RentabClienteSet?$filter=SalesOrgId%20eq%20%2750000005%27%20and%20TrqType%20eq%20%27OPTN%27"
            + "%20and%20OrderPartyId%20eq%20%2715927%27&$format=json&sap-client=100");
    }

    [Fact]
    public async Task ToleraUnaUrlBaseConBarraFinal()
    {
        _sap.ResponderFilas(_filaOptn);

        await Fuente(url: clsSapTmFalso.UrlBase + "/").ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        _sap.Peticiones.Single().Url.AbsolutePath.Should().Be("/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV/RentabClienteSet");
    }

    // test_odata_vacio_devuelve_none
    [Fact]
    public async Task SinResultadosDevuelveNull()
    {
        _sap.ResponderFilas();

        (await Fuente().ObtenerValorAsync("50000005", "OPTN", "404", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task AceptaElValueComoNumeroJson()
    {
        _sap.ResponderFilas(new { SalesOrgId = "50000005", TrqType = "OPTN", OrderPartyId = "15927", Value = 13.5m });

        (await Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct)).Should().Be(13.5m);
    }

    [Fact]
    public async Task ConVariasFilasTomaLaPrimeraComoElPython()
    {
        _sap.ResponderFilas(_filaOptn, new { SalesOrgId = "50000005", TrqType = "OPTN", OrderPartyId = "15927", Value = "99" });

        (await Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct)).Should().Be(13.5m);
    }

    // Corrige la inyección del Python: el filtro se armaba con f-string sin escapar las comillas.
    [Theory]
    [InlineData("15927' or 1 eq 1", "OrderPartyId eq '15927'' or 1 eq 1'")]
    [InlineData("15927') or (1 eq 1", "OrderPartyId eq '15927'') or (1 eq 1'")]
    [InlineData("O'Brien", "OrderPartyId eq 'O''Brien'")]
    public async Task EscapaLasComillasDelBpParaQueNoAltereElFiltro(string bp, string terminaEn)
    {
        var valor = await Fuente().ObtenerValorAsync("50000005", "OPTN", bp, Ct);

        valor.Should().BeNull("el BP inyectado es un literal que no existe, no una condición verdadera");
        var peticion = _sap.Peticiones.Should().ContainSingle().Subject;
        clsSapTmFalso.FiltroDe(peticion.Url).Should().Be($"SalesOrgId eq '50000005' and TrqType eq 'OPTN' and {terminaEn}");
        peticion.Url.Query.Should().NotContain("'").And.NotContain(" ");
    }

    [Fact]
    public async Task CodificaLaUrlParaQueElBpNoAgregueParametros()
    {
        await Fuente().ObtenerValorAsync("50000005", "OPTN", "1&$top=1000#x", Ct);

        var url = _sap.Peticiones.Single().Url;
        url.Query.Should().EndWith("&$format=json").And.Contain("%26%24top%3D1000%23x");
        url.Fragment.Should().BeEmpty();
        clsSapTmFalso.FiltroDe(url).Should().EndWith("OrderPartyId eq '1&$top=1000#x'");
    }

    [Fact]
    public async Task EscapaTambienLaOrganizacionYElTrqType()
    {
        await Fuente().ObtenerValorAsync("5' or '1'='1", "OP'TN", "15927", Ct);

        clsSapTmFalso.FiltroDe(_sap.Peticiones.Single().Url).Should()
            .Be("SalesOrgId eq '5'' or ''1''=''1' and TrqType eq 'OP''TN' and OrderPartyId eq '15927'");
    }

    // test_odata_error_500_es_estructurado
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task UnErrorHttpEsFuenteNoDisponibleSinExponerCredenciales(HttpStatusCode estado)
    {
        _sap.Responder(estado, "{\"error\":\"detalle interno\"}");

        var consultar = () => Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        var mensaje = (await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>()).Which.Message;
        mensaje.Should().Contain(((int)estado).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain(Clave).And.NotContain(Usuario);
    }

    // test_odata_timeout_es_estructurado
    [Fact]
    public async Task UnTimeoutEsFuenteNoDisponible()
    {
        _sap.Fallar(new TaskCanceledException("timeout", new TimeoutException()));

        var consultar = () => Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();
    }

    [Fact]
    public async Task UnTimeoutDejaUnaLineaDeWarningSinTrazaYLaExcepcionSoloEnDebug()
    {
        var logger = new clsLoggerFalso<clsFuenteMargenOData>();
        _sap.Fallar(new TaskCanceledException("timeout", new TimeoutException()));

        var consultar = () => Fuente(logger: logger).ObtenerValorAsync("50000005", "OPTN", "15927", Ct);
        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();

        logger.Registros.Should().HaveCount(2);
        logger.Registros[0].Should().Match<clsRegistroLog>(r =>
            r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Excepcion == null && r.Mensaje.Contains("tiempo agotado"));
        logger.Registros[1].Should().Match<clsRegistroLog>(r =>
            r.Nivel == Microsoft.Extensions.Logging.LogLevel.Debug && r.Excepcion is TaskCanceledException);
        logger.Registros.Should().OnlyContain(r => !r.Mensaje.Contains(Clave) && !r.Mensaje.Contains(Usuario));
    }

    [Fact]
    public async Task UnErrorDeRedEsFuenteNoDisponible()
    {
        _sap.Fallar(new HttpRequestException("Connection refused"));

        var consultar = () => Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorNoSeDisfrazaDeCaidaDeSap()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var consultar = () => Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", cancelacion.Token);

        (await consultar.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeOfType<clsFuenteMargenNoDisponibleException>();
    }

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{}")]
    [InlineData("{\"d\":{}}")]
    [InlineData("{\"d\":{\"results\":{}}}")]
    [InlineData("{\"d\":{\"results\":[{\"SalesOrgId\":\"50000005\"}]}}")]
    [InlineData("{\"d\":{\"results\":[{\"Value\":null}]}}")]
    [InlineData("{\"d\":{\"results\":[{\"Value\":\"trece\"}]}}")]
    public async Task UnaRespuestaInvalidaEsFuenteNoDisponible(string cuerpo)
    {
        _sap.Responder(HttpStatusCode.OK, cuerpo);

        var consultar = () => Fuente().ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        (await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>())
            .Which.Message.Should().StartWith("SAP TM no disponible o respuesta inválida");
    }

    [Fact]
    public void ConUrlEstaConfiguradaYSuModoEsOData()
    {
        Fuente().Modo.Should().Be("odata");
        Fuente().EstaConfigurada.Should().BeTrue();
        Fuente(url: null).EstaConfigurada.Should().BeFalse();
    }

    public void Dispose()
    {
        _http.Dispose();
        _sap.Dispose();
    }

    private clsFuenteMargenOData Fuente(
        string? url = clsSapTmFalso.UrlBase, string? sapClient = null, Microsoft.Extensions.Logging.ILogger<clsFuenteMargenOData>? logger = null) =>
        new(
            _http,
            Options.Create(new clsOpcionesSapTm
            {
                Modo = clsOpcionesSapTm.ModoOData, Url = url, User = Usuario, Pass = Clave, SapClient = sapClient,
            }),
            logger ?? NullLogger<clsFuenteMargenOData>.Instance);
}
