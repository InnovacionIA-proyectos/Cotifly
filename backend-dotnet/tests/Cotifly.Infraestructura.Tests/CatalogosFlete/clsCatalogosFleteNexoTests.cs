using System.Net;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.CatalogosFlete.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.CatalogosFlete;

/// <summary>
/// <see cref="clsCatalogosFleteNexo"/>: consumo de <c>GET /api/biblioteca-sap-historico/filtros</c> contra un Nexo
/// falso (<see cref="clsNexoClientesFalso"/>, reutilizado tal cual: es genérico en URL y respuesta): nunca sale a la
/// red. Aquí el adaptador recibe un <see cref="HttpClient"/> directo, sin la tubería de resiliencia (esa se prueba en
/// <c>clsRegistroCatalogosFleteTests</c>).
/// </summary>
public sealed class clsCatalogosFleteNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsCatalogosFleteNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    [Fact]
    public async Task ConstruyeLaUrlSinQueryYMandaElIdExternoComoIdPortal()
    {
        _nexo.ResponderCandidatos("{}");

        await Proveedor().ObtenerAsync(_usuarioId, Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Get);
        peticion.Url.AbsoluteUri.Should().Be(clsNexoClientesFalso.UrlBase + "/api/biblioteca-sap-historico/filtros");
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task MapeaLosTresCatalogosYDescartaLineasNegocio()
    {
        _nexo.ResponderCandidatos(
            """
            {
              "lineasNegocio": [{"valor":"carga","etiqueta":"Carga"}],
              "tiposVehiculo": [{"valor":"NHR","etiqueta":"NHR"}],
              "tiposCarroseria": [{"valor":"PLA","etiqueta":"Platón"}],
              "clasesViaje": [{"valor":"ida","etiqueta":"Solo ida"}, {"valor":"redondo","etiqueta":"Ida y vuelta"}]
            }
            """);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TiposVehiculo.Should().ContainSingle().Which.Should().Match<clsOpcionCatalogo>(o => o.Valor == "NHR" && o.Etiqueta == "NHR");
        resultado.Valor.TiposCarroceria.Should().ContainSingle().Which.Should().Match<clsOpcionCatalogo>(o => o.Valor == "PLA" && o.Etiqueta == "Platón");
        resultado.Valor.ClasesViaje.Should().HaveCount(2);
    }

    [Fact]
    public async Task DescartaOpcionesSinValorOEtiqueta()
    {
        _nexo.ResponderCandidatos(
            """
            {"tiposVehiculo": [{"etiqueta":"Sin valor"},{"valor":"Sin etiqueta"},{"valor":"OK","etiqueta":"Ok"}],
             "tiposCarroseria": [], "clasesViaje": []}
            """);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.TiposVehiculo.Should().ContainSingle().Which.Valor.Should().Be("OK");
    }

    [Fact]
    public async Task SinCatalogosEnLaRespuestaDevuelveListasVacias()
    {
        _nexo.ResponderCandidatos("{}");

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TiposVehiculo.Should().BeEmpty();
        resultado.Valor.TiposCarroceria.Should().BeEmpty();
        resultado.Valor.ClasesViaje.Should().BeEmpty();
    }

    [Fact]
    public async Task SinUrlBaseConfiguradaFallaSinLlamarANexo()
    {
        var resultado = await Proveedor(url: null).ObtenerAsync(_usuarioId, Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task SinElUsuarioEnCotiflyFallaSinLlamarANexo()
    {
        UsuarioResuelto(null);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElUsuarioSinIdUsuarioExternoFallaSinLlamarANexo()
    {
        UsuarioResuelto(UsuarioConIdExterno(null));

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnErrorHttpEsCatalogosFleteNoDisponible(HttpStatusCode estado)
    {
        _nexo.Responder(estado);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task UnJsonInvalidoEsCatalogosFleteNoDisponible()
    {
        _nexo.Responder(HttpStatusCode.OK, "no es json");

        (await Proveedor().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task UnErrorDeRedEsCatalogosFleteNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        (await Proveedor().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task UnFalloDejaUnaSolaLineaDeWarningSinTraza()
    {
        var logger = new clsLoggerFalso<clsCatalogosFleteNexo>();
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        await Proveedor(logger: logger).ObtenerAsync(_usuarioId, Ct);

        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Excepcion == null);
        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Debug && r.Excepcion is HttpRequestException);
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorNoSeDisfrazaDeCaidaDeNexo()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var consultar = () => Proveedor().ObtenerAsync(_usuarioId, cancelacion.Token);

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

    private clsCatalogosFleteNexo Proveedor(
        string? url = clsNexoClientesFalso.UrlBase, Microsoft.Extensions.Logging.ILogger<clsCatalogosFleteNexo>? logger = null) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, logger ?? NullLogger<clsCatalogosFleteNexo>.Instance);
}
