using System.Net;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Municipios.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Municipios;

/// <summary>
/// <see cref="clsMunicipiosNexo"/>: consumo de <c>GET /api/captura-solicitudes/municipios</c> contra un Nexo falso
/// (<see cref="clsNexoClientesFalso"/>, reutilizado tal cual: es genérico en URL y respuesta): nunca sale a la red.
/// Aquí el adaptador recibe un <see cref="HttpClient"/> directo, sin la tubería de resiliencia (esa se prueba en
/// <c>clsRegistroMunicipiosTests</c>).
/// </summary>
public sealed class clsMunicipiosNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsMunicipiosNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    [Fact]
    public async Task ConstruyeLaUrlSinQueryYMandaElIdExternoComoIdPortal()
    {
        _nexo.ResponderCandidatos("[]");

        await Proveedor().ObtenerAsync(_usuarioId, Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Get);
        peticion.Url.AbsoluteUri.Should().Be(clsNexoClientesFalso.UrlBase + "/api/captura-solicitudes/municipios");
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task LaBusquedaVaContraCiudadesConElTextoYElDepartamentoYCompletaElPais()
    {
        // HU-023 (#176, contrato con Nexo §3.1): los ids de origen/destino salen de GET /api/captura-solicitudes/ciudades (sin "pais").
        _nexo.ResponderCandidatos(
            """
            [{"id":5001,"codigoDane":"05001","municipio":"Medellín","departamento":"Antioquia","nombreTarifasPlaza":null,"zonaTarificacion":null}]
            """);

        var resultado = await Proveedor().BuscarAsync(_usuarioId, " medellín ", "Antioquia", Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Url.AbsolutePath.Should().Be("/api/captura-solicitudes/ciudades");
        peticion.Url.Query.Should().Be("?departamento=Antioquia&q=medell%C3%ADn");
        peticion.IdPortalUsuario.Should().Be("1007");
        resultado.Valor.Should().ContainSingle().Which.Should().Be(new clsMunicipio(5001, "05001", "Medellín", "Antioquia", "Colombia"));
    }

    [Fact]
    public void LaBusquedaSinFiltrosNoMandaQuery() =>
        clsMunicipiosNexo.ConstruirUrlCiudades(clsNexoClientesFalso.UrlBase, " ", null).AbsoluteUri
            .Should().Be(clsNexoClientesFalso.UrlBase + "/api/captura-solicitudes/ciudades");

    [Fact]
    public async Task MapeaLosMunicipiosConTodosSusCampos()
    {
        _nexo.ResponderCandidatos(
            """
            [{"id":1,"codigoDane":"05001","municipio":"Medellín","departamento":"Antioquia","pais":"Colombia",
              "nombreTarifasPlaza":"MEDELLIN","zonaTarificacion":"Z1"}]
            """);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var municipio = resultado.Valor.Should().ContainSingle().Subject;
        municipio.Id.Should().Be(1);
        municipio.CodigoDane.Should().Be("05001");
        municipio.Municipio.Should().Be("Medellín");
        municipio.Departamento.Should().Be("Antioquia");
        municipio.Pais.Should().Be("Colombia");
    }

    [Fact]
    public async Task DescartaFilasSinIdNumerico()
    {
        _nexo.ResponderCandidatos(
            """
            [{"municipio":"Sin id"},{"id":"no-numerico","municipio":"Id como texto"},
             {"id":2,"municipio":"Bogotá","departamento":"Cundinamarca","pais":"Colombia"}]
            """);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.Should().ContainSingle().Which.Id.Should().Be(2);
    }

    [Fact]
    public async Task SinUrlBaseConfiguradaFallaSinLlamarANexo()
    {
        var resultado = await Proveedor(url: null).ObtenerAsync(_usuarioId, Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task SinElUsuarioEnCotiflyFallaSinLlamarANexo()
    {
        UsuarioResuelto(null);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElUsuarioSinIdUsuarioExternoFallaSinLlamarANexo()
    {
        UsuarioResuelto(UsuarioConIdExterno(null));

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnErrorHttpEsMunicipiosNoDisponible(HttpStatusCode estado)
    {
        _nexo.Responder(estado);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
    }

    [Fact]
    public async Task UnJsonInvalidoEsMunicipiosNoDisponible()
    {
        _nexo.Responder(HttpStatusCode.OK, "no es json");

        (await Proveedor().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":[{"id":1,"municipio":"Bogotá"}]}""")]
    [InlineData("null")]
    [InlineData("\"texto\"")]
    public async Task UnDoscientosQueNoEsUnArregloEsMunicipiosNoDisponible(string json)
    {
        _nexo.Responder(HttpStatusCode.OK, json);

        (await Proveedor().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
    }

    [Fact]
    public async Task UnErrorDeRedEsMunicipiosNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        (await Proveedor().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
    }

    [Fact]
    public async Task UnFalloDejaUnaSolaLineaDeWarningSinTraza()
    {
        var logger = new clsLoggerFalso<clsMunicipiosNexo>();
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

    private clsMunicipiosNexo Proveedor(
        string? url = clsNexoClientesFalso.UrlBase, Microsoft.Extensions.Logging.ILogger<clsMunicipiosNexo>? logger = null) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, logger ?? NullLogger<clsMunicipiosNexo>.Instance);
}
