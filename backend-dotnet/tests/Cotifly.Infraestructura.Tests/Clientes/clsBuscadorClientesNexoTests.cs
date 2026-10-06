using System.Net;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Clientes;

/// <summary>
/// <see cref="clsBuscadorClientesNexo"/>: consumo de <c>GET /api/captura-solicitudes/clientes?q=</c> contra un Nexo falso
/// (<see cref="clsNexoClientesFalso"/>): nunca sale a la red. Aquí el adaptador recibe un <see cref="HttpClient"/> directo, sin
/// la tubería de resiliencia (esa se prueba en <c>clsRegistroBuscadorClientesTests</c>).
/// </summary>
public sealed class clsBuscadorClientesNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsBuscadorClientesNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    [Fact]
    public async Task ConstruyeLaUrlCodificadaYMandaElIdExternoComoIdPortal()
    {
        _nexo.ResponderCandidatos("[]");

        await Buscador().BuscarAsync(_usuarioId, "Postobón & Cía", Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Get);
        peticion.Url.AbsoluteUri.Should().Be(clsNexoClientesFalso.UrlBase + "/api/captura-solicitudes/clientes?q=Postob%C3%B3n%20%26%20C%C3%ADa");
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task MapeaLosCandidatosPrefiriendoNitClienteSobreNit()
    {
        _nexo.ResponderCandidatos(
            """
            [{"clienteId":"43115293-2c68-4468-8d4b-6b9a899a4921","bp":null,"nit":"890903939-1","nitCliente":"890903939-1","nombre":"Postobón S.A.","origen":"cliente"}]
            """);

        var resultado = await Buscador().BuscarAsync(_usuarioId, "postobon", Ct);

        resultado.EsExitoso.Should().BeTrue();
        var candidato = resultado.Valor.Candidatos.Should().ContainSingle().Subject;
        candidato.NitCliente.Should().Be("890903939-1");
        candidato.Nombre.Should().Be("Postobón S.A.");
        candidato.OrigenCliente.Should().Be("cliente");
        candidato.Bp.Should().BeNull();
    }

    [Fact]
    public async Task SinNitClienteUsaNitComoRespaldo()
    {
        _nexo.ResponderCandidatos("""[{"nit":"900123456","nombre":"LONGYIN S.A.S.","origen":"cliente"}]""");

        var resultado = await Buscador().BuscarAsync(_usuarioId, "longyin", Ct);

        resultado.Valor.Candidatos.Should().ContainSingle().Which.NitCliente.Should().Be("900123456");
    }

    [Fact]
    public async Task DescartaFilasSinNitONombreONiOrigen()
    {
        _nexo.ResponderCandidatos(
            """
            [{"nombre":"Sin NIT","origen":"cliente"},{"nit":"1","origen":"cliente"},{"nit":"2","nombre":"Sin origen"},
             {"nit":"900123456","nombre":"LONGYIN S.A.S.","origen":"cliente"}]
            """);

        var resultado = await Buscador().BuscarAsync(_usuarioId, "x", Ct);

        resultado.Valor.Candidatos.Should().ContainSingle().Which.NitCliente.Should().Be("900123456");
    }

    [Fact]
    public async Task ConUnSoloCandidatoEsExacta()
    {
        _nexo.ResponderCandidatos("""[{"nit":"900123456","nombre":"LONGYIN S.A.S.","origen":"cliente"}]""");

        (await Buscador().BuscarAsync(_usuarioId, "longyin", Ct)).Valor.Exacta.Should().BeTrue();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"nit":"1","nombre":"A","origen":"cliente"},{"nit":"2","nombre":"B","origen":"prospecto"}]""")]
    public async Task SinCandidatosOConVariosNoEsExacta(string cuerpo)
    {
        _nexo.ResponderCandidatos(cuerpo);

        (await Buscador().BuscarAsync(_usuarioId, "x", Ct)).Valor.Exacta.Should().BeFalse();
    }

    [Fact]
    public async Task SinUrlBaseConfiguradaFallaSinLlamarANexo()
    {
        var resultado = await Buscador(url: null).BuscarAsync(_usuarioId, "x", Ct);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task SinElUsuarioEnCotiflyFallaSinLlamarANexo()
    {
        UsuarioResuelto(null);

        var resultado = await Buscador().BuscarAsync(_usuarioId, "x", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElUsuarioSinIdUsuarioExternoFallaSinLlamarANexo()
    {
        UsuarioResuelto(UsuarioConIdExterno(null));

        var resultado = await Buscador().BuscarAsync(_usuarioId, "x", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnErrorHttpEsBusquedaNoDisponible(HttpStatusCode estado)
    {
        _nexo.Responder(estado);

        var resultado = await Buscador().BuscarAsync(_usuarioId, "x", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
    }

    [Fact]
    public async Task UnJsonInvalidoEsBusquedaNoDisponible()
    {
        _nexo.Responder(HttpStatusCode.OK, "no es json");

        (await Buscador().BuscarAsync(_usuarioId, "x", Ct)).Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
    }

    [Fact]
    public async Task UnErrorDeRedEsBusquedaNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        (await Buscador().BuscarAsync(_usuarioId, "x", Ct)).Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
    }

    [Fact]
    public async Task UnFalloDejaUnaSolaLineaDeWarningSinTraza()
    {
        var logger = new clsLoggerFalso<clsBuscadorClientesNexo>();
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        await Buscador(logger: logger).BuscarAsync(_usuarioId, "x", Ct);

        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Excepcion == null);
        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Debug && r.Excepcion is HttpRequestException);
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorNoSeDisfrazaDeCaidaDeNexo()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var consultar = () => Buscador().BuscarAsync(_usuarioId, "x", cancelacion.Token);

        (await consultar.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public void SinUrlBaseNoEstaConfigurada()
    {
        Buscador(url: null).EstaConfigurada.Should().BeFalse();
        Buscador().EstaConfigurada.Should().BeTrue();
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

    private clsBuscadorClientesNexo Buscador(
        string? url = clsNexoClientesFalso.UrlBase, Microsoft.Extensions.Logging.ILogger<clsBuscadorClientesNexo>? logger = null) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, logger ?? NullLogger<clsBuscadorClientesNexo>.Instance);
}
