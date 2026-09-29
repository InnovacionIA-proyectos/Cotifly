using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// <see cref="clsCosteoNexo"/> contra un <see cref="HttpMessageHandler"/> falso (<see cref="clsNexoClientesFalso"/>): alta de la
/// solicitud (HU-005.5). Sin la tubería de resiliencia (esa se prueba en <c>clsRegistroCosteoNexoTests</c>); nunca sale a la red.
/// </summary>
public sealed class clsCosteoNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsCosteoNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsSolicitudNexo Solicitud() => new("15927", clsSolicitudCosteoMuestra.Crear(), "PuertoAInterior");

    private const string Creada =
        """{"id":"3f2b8c1e-0000-4000-8000-000000000001","lineas":1,"rutasCreadas":0,"vehiculosCreados":1,"scorePrioridad":0.73,"fletesCalculados":0,"lineasPorCostear":1,"costeoEnProceso":false}""";

    [Fact]
    public async Task ElAltaVaAApiCapturaSolicitudesConElIdExternoComoIdPortal()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);

        await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/captura-solicitudes", "Nexo__UrlBase es la raíz, sin /api (ADR-015 §8)");
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task ElCuerpoEsCapturaSolicitudRequestEnCamelCaseConUnaLineaYSinNulos()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);

        await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        var raiz = cuerpo.RootElement;
        raiz.GetProperty("clienteBp").GetString().Should().Be("15927");
        raiz.GetProperty("tipo").GetString().Should().Be("spot", "Nexo valida el tipo en minúscula");
        raiz.GetProperty("deadline").GetDateTimeOffset().Should().Be(clsSolicitudCosteoMuestra.Deadline);
        raiz.GetProperty("valorEstimado").GetDecimal().Should().Be(12_000_000m);
        raiz.GetProperty("referencia").GetString().Should().Be("OC-7788");
        raiz.GetProperty("horasDescargue").GetDecimal().Should().Be(6.5m);
        raiz.TryGetProperty("clienteId", out _).Should().BeFalse("los nulos no viajan");
        raiz.TryGetProperty("modalidad", out _).Should().BeFalse("la modalidad no aplica en Cotifly (mapeo aprobado)");

        var linea = raiz.GetProperty("lineas").EnumerateArray().Should().ContainSingle().Subject;
        linea.GetProperty("origen").GetString().Should().Be("Buenaventura");
        linea.GetProperty("destino").GetString().Should().Be("Bogotá D.C.");
        linea.GetProperty("tipoVehiculo").GetString().Should().Be("3S3");
        linea.GetProperty("carroceria").GetString().Should().Be("PORTACONTENEDORES");
        linea.GetProperty("lineaNegocio").GetString().Should().Be("PuertoAInterior");
        linea.GetProperty("claseContenedor").GetString().Should().Be("CNT40.HC");
        linea.GetProperty("exclusivo").GetBoolean().Should().BeTrue();
        linea.GetProperty("retornaContenedor").GetBoolean().Should().BeTrue();
        linea.GetProperty("peso").GetDecimal().Should().Be(28_500.5m, "Nexo llama 'peso' al peso de la línea en kg");
        linea.GetProperty("valorMercancia").GetDecimal().Should().Be(250_000_000m);
        linea.TryGetProperty("valorAprovisionamiento", out _).Should().BeFalse();

        var posiciones = linea.GetProperty("posiciones").EnumerateArray().ToList();
        posiciones.Should().HaveCount(2);
        posiciones[0].GetProperty("descripcion").GetString().Should().Be("Estiba de café");
        posiciones[0].GetProperty("cantidad").GetInt32().Should().Be(20);
        posiciones[0].GetProperty("largoCm").GetDecimal().Should().Be(120m);
        posiciones[0].GetProperty("pesoKg").GetDecimal().Should().Be(1425m);
        posiciones[1].TryGetProperty("descripcion", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SinPosicionesNoMandaElArreglo()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var solicitud = Solicitud() with { Solicitud = clsSolicitudCosteoMuestra.Crear() with { Posiciones = [] } };

        await Adaptador().EnviarSolicitudAsync(_usuarioId, solicitud, Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        cuerpo.RootElement.GetProperty("lineas")[0].TryGetProperty("posiciones", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Un201DevuelveElIdYSiNexoYaCosteoLaLinea()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada.Replace("\"fletesCalculados\":0,\"lineasPorCostear\":1", "\"fletesCalculados\":1,\"lineasPorCostear\":0", StringComparison.Ordinal));

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Valor.Id.Should().Be("3f2b8c1e-0000-4000-8000-000000000001");
        resultado.Valor.FletesCalculados.Should().Be(1);
        resultado.Valor.CosteoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task ConLineasPorCostearOCosteoEnProcesoNoEstaCompleto()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        _nexo.Responder(HttpStatusCode.Created, Creada.Replace("\"lineasPorCostear\":1,\"costeoEnProceso\":false", "\"lineasPorCostear\":0,\"costeoEnProceso\":true", StringComparison.Ordinal));

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Valor.CosteoCompleto.Should().BeFalse();
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Valor.CosteoCompleto.Should().BeFalse();
    }

    [Fact]
    public async Task Un400DelAltaEsSolicitudInvalidaDefinitivaConElCodigoDeNexo()
    {
        _nexo.Responder(HttpStatusCode.BadRequest, """{"code":"PESO_INCONSISTENTE","message":"No coincide."}""");

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoSolicitudInvalida);
        resultado.Error.Detalle.Should().Be("HTTP 400 PESO_INCONSISTENTE: No coincide.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "SESION_INVALIDA", "Identidad no válida.")]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO_CABINA_FLETES", "Sin permiso.")]
    public async Task Un4xxEsRechazadaConElCodigoYElMensajeDeNexo(HttpStatusCode estado, string codigo, string mensaje)
    {
        _nexo.Responder(estado, JsonSerializer.Serialize(new { code = codigo, message = mensaje, timestamp = "2026-09-28T15:00:00Z" }));

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
        resultado.Error.Detalle.Should().Be($"HTTP {(int)estado} {codigo}: {mensaje}");
    }

    [Fact]
    public async Task Un4xxSinCuerpoJsonUsaElTexto()
    {
        _nexo.Responder(HttpStatusCode.NotFound, "no encontrado");

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("HTTP 404: no encontrado");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Un5xxEsNoDisponible(HttpStatusCode estado)
    {
        _nexo.Responder(estado);

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        resultado.Error.Detalle.Should().StartWith($"HTTP {(int)estado}");
    }

    [Fact]
    public async Task UnTiempoAgotadoAvisaQueLaSolicitudPudoQuedarCreada()
    {
        _nexo.Fallar(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoTiempoAgotado);
        resultado.Error.Mensaje.Should().Contain("pudo quedar creada");
    }

    [Fact]
    public async Task UnErrorDeRedOUnJsonInvalidoEsNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("Connection refused"));
        _nexo.Responder(HttpStatusCode.Created, "no es json");
        _nexo.Responder(HttpStatusCode.Created, """{"lineas":1}""");

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("error de red");
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("JSON inválido");
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("respuesta sin id");
    }

    [Fact]
    public async Task SinUrlBaseOSinIdExternoFallaSinLlamarANexo()
    {
        (await Adaptador(url: null).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        UsuarioResuelto(UsuarioConIdExterno(null));
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Contain("idUsuarioExterno");

        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task UnFalloDejaUnaSolaLineaDeWarningSinTraza()
    {
        var logger = new clsLoggerFalso<clsCosteoNexo>();
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        await Adaptador(logger: logger).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Excepcion == null);
        logger.Registros.Should().NotContain(r => r.Mensaje.Contains("1007", StringComparison.Ordinal), "el id del portal no va al log");
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorSePropaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var enviar = () => Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), cancelacion.Token);

        await enviar.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void EstaConfiguradoSoloConUrlBase()
    {
        Adaptador(url: null).EstaConfigurado.Should().BeFalse();
        Adaptador(url: "  ").EstaConfigurado.Should().BeFalse();
        Adaptador().EstaConfigurado.Should().BeTrue();
    }

    [Fact]
    public async Task UnaUrlBaseConBarraFinalNoDuplicaLaBarra()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);

        await Adaptador(url: clsNexoClientesFalso.UrlBase + "/").EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        _nexo.Peticiones.Single().Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/captura-solicitudes");
    }

    private void UsuarioResuelto(clsUsuarioWeb? usuario) =>
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(usuario);

    private static clsUsuarioWeb UsuarioConIdExterno(long? idUsuarioExterno) =>
        clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, idUsuarioExterno).Valor;

    private clsCosteoNexo Adaptador(string? url = clsNexoClientesFalso.UrlBase, Microsoft.Extensions.Logging.ILogger<clsCosteoNexo>? logger = null) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, logger ?? NullLogger<clsCosteoNexo>.Instance);

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
