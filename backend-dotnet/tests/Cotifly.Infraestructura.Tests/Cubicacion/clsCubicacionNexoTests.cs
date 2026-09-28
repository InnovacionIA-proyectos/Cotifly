using System.Net;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Cubicacion;

/// <summary>
/// <see cref="clsCubicacionNexo"/>: consumo de <c>POST /api/cubicacion</c> contra un Nexo falso
/// (<see cref="clsNexoClientesFalso"/>, reutilizado tal cual: es genérico en URL, cuerpo y respuesta): nunca sale a la
/// red. Aquí el adaptador recibe un <see cref="HttpClient"/> directo, sin la tubería de resiliencia (esa se prueba en
/// <c>clsRegistroCubicacionTests</c>).
/// </summary>
public sealed class clsCubicacionNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsCubicacionNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static IReadOnlyList<clsItemCubicacion> UnItem() => [new clsItemCubicacion("Caja", 2, 100, 80, 60, 50)];

    private const string RespuestaValida =
        """
        {
          "matrices": [{"unidad": {"id": "IGNORAR"}, "criterios": [], "banda": "verde", "advertencias": [], "disposicion": null}],
          "sugerencia": {
            "unidadId": "NHR", "unidadNombre": "NHR Furgón", "banda": "verde", "factorUtilizacion": 0.62,
            "configVehiculo": "2 ejes", "carroceria": "Furgón", "claseViaje": "ida", "claseContenedor": null
          },
          "advertencias": ["Revisa el peso declarado"],
          "sugerenciaFlota": {
            "unidadId": "NHR", "unidadNombre": "NHR Furgón", "cantidadVehiculos": 2, "utilizacionPromedio": 0.71,
            "configVehiculo": "2 ejes", "carroceria": "Furgón", "claseViaje": "ida", "claseContenedor": null,
            "disposiciones": [{"pesoTotalKg": 900, "centroGravedadM": [1,1,1], "margenEstabilidadOk": true, "factorUtilizacionVolumen": 0.7, "itemsSinUbicar": [], "colocaciones": []}]
          },
          "extradimension": {
            "hayExtradimension": true,
            "medidasEstandar": {"largoMaxCm": 600, "anchoMaxCm": 240, "altoMaxCm": 240, "pesoMaxKg": 8000},
            "piezas": [{"descripcion": "Viga", "excesos": ["largo"], "largoCm": 700, "anchoCm": 100, "altoCm": 100, "pesoKg": 500}],
            "sugerencia": "Cotiza como extradimensionada"
          }
        }
        """;

    [Fact]
    public async Task ConstruyeLaUrlYMandaElIdExternoComoIdPortal()
    {
        _nexo.Responder(HttpStatusCode.OK, RespuestaValida);

        await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsoluteUri.Should().Be(clsNexoClientesFalso.UrlBase + "/api/cubicacion");
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task MandaLosItemsEnCamelCaseYUnidadIdsSiempreNulo()
    {
        _nexo.Responder(HttpStatusCode.OK, RespuestaValida);

        await Proveedor().CubicarAsync(_usuarioId, UnItem(), "ida", Ct);

        var cuerpo = _nexo.Peticiones.Should().ContainSingle().Subject.Cuerpo;
        cuerpo.Should().Contain("\"largoCm\":100").And.Contain("\"anchoCm\":80").And.Contain("\"altoCm\":60").And.Contain("\"pesoKg\":50");
        cuerpo.Should().Contain("\"cantidad\":2").And.Contain("\"descripcion\":\"Caja\"");
        cuerpo.Should().Contain("\"unidadIds\":null");
        cuerpo.Should().Contain("\"claseViaje\":\"ida\"");
    }

    [Fact]
    public async Task MapeaSugerenciaSugerenciaFlotaAdvertenciasYExtradimensionDescartandoMatricesYDisposiciones()
    {
        _nexo.Responder(HttpStatusCode.OK, RespuestaValida);

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var valor = resultado.Valor;

        valor.Sugerencia.Should().Match<clsSugerenciaVehiculo>(s =>
            s.UnidadId == "NHR" && s.UnidadNombre == "NHR Furgón" && s.Banda == "verde" && s.FactorUtilizacion == 0.62m
            && s.ConfigVehiculo == "2 ejes" && s.Carroceria == "Furgón" && s.ClaseViaje == "ida" && s.ClaseContenedor == null);

        valor.SugerenciaFlota.Should().Match<clsSugerenciaFlota>(f =>
            f.UnidadId == "NHR" && f.CantidadVehiculos == 2 && f.UtilizacionPromedio == 0.71m);

        valor.Advertencias.Should().ContainSingle().Which.Should().Be("Revisa el peso declarado");

        valor.Extradimension.Should().NotBeNull();
        valor.Extradimension!.HayExtradimension.Should().BeTrue();
        valor.Extradimension.MedidasEstandar.Should().Match<clsMedidasEstandar>(m => m.LargoMaxCm == 600 && m.PesoMaxKg == 8000);
        valor.Extradimension.Piezas.Should().ContainSingle().Which.Should().Match<clsPiezaExcedida>(p => p.Descripcion == "Viga" && p.Excesos.Contains("largo"));
        valor.Extradimension.Sugerencia.Should().Be("Cotiza como extradimensionada");
    }

    [Fact]
    public async Task SinSugerenciaNiFlotaNiExtradimensionDevuelveNulosYAdvertenciasVacias()
    {
        _nexo.Responder(HttpStatusCode.OK, """{"matrices":[],"sugerencia":null,"advertencias":[],"sugerenciaFlota":null,"extradimension":null}""");

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Sugerencia.Should().BeNull();
        resultado.Valor.SugerenciaFlota.Should().BeNull();
        resultado.Valor.Extradimension.Should().BeNull();
        resultado.Valor.Advertencias.Should().BeEmpty();
    }

    [Fact]
    public async Task Un400DeNexoEsCubicacionDemasiadoGrandeConElMensajeDeNexoComoDetalle()
    {
        _nexo.Responder(HttpStatusCode.BadRequest, """{"code":"CUBICACION_INVALIDA","message":"Cubicador rechazó la solicitud: demasiados bultos","timestamp":"2026-01-01T00:00:00Z"}""");

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionDemasiadoGrande);
        resultado.Error.Detalle.Should().Be("Cubicador rechazó la solicitud: demasiados bultos");
    }

    [Fact]
    public async Task Un400SinCuerpoJsonUsaElTextoCrudoComoDetalle()
    {
        _nexo.Responder(HttpStatusCode.BadRequest, "no es json");

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionDemasiadoGrande);
        resultado.Error.Detalle.Should().Be("no es json");
    }

    [Fact]
    public async Task SinUrlBaseConfiguradaFallaSinLlamarANexo()
    {
        var resultado = await Proveedor(url: null).CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task SinElUsuarioEnCotiflyFallaSinLlamarANexo()
    {
        UsuarioResuelto(null);

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElUsuarioSinIdUsuarioExternoFallaSinLlamarANexo()
    {
        UsuarioResuelto(UsuarioConIdExterno(null));

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnErrorHttpDistintoDe400EsCubicacionNoDisponible(HttpStatusCode estado)
    {
        _nexo.Responder(estado);

        var resultado = await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
    }

    [Fact]
    public async Task UnJsonInvalidoEnUn200EsCubicacionNoDisponible()
    {
        _nexo.Responder(HttpStatusCode.OK, "no es json");

        (await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
    }

    [Fact]
    public async Task UnErrorDeRedEsCubicacionNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        (await Proveedor().CubicarAsync(_usuarioId, UnItem(), null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicacionNoDisponible);
    }

    [Fact]
    public async Task UnFalloDejaUnaSolaLineaDeWarningSinTraza()
    {
        var logger = new clsLoggerFalso<clsCubicacionNexo>();
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        await Proveedor(logger: logger).CubicarAsync(_usuarioId, UnItem(), null, Ct);

        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Excepcion == null);
        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Debug && r.Excepcion is HttpRequestException);
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorNoSeDisfrazaDeCaidaDeNexo()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var consultar = () => Proveedor().CubicarAsync(_usuarioId, UnItem(), null, cancelacion.Token);

        (await consultar.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public void SinUrlBaseNoEstaConfigurada()
    {
        Proveedor(url: null).EstaConfigurada.Should().BeFalse();
        Proveedor().EstaConfigurada.Should().BeTrue();
    }

    private void UsuarioResuelto(clsUsuarioWeb? usuario) =>
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(usuario);

    private static clsUsuarioWeb UsuarioConIdExterno(long? idUsuarioExterno) =>
        clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, idUsuarioExterno).Valor;

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }

    private clsCubicacionNexo Proveedor(
        string? url = clsNexoClientesFalso.UrlBase, Microsoft.Extensions.Logging.ILogger<clsCubicacionNexo>? logger = null) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, logger ?? NullLogger<clsCubicacionNexo>.Instance);
}
