using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>
/// Regional efectiva (ADR-016 §6): <c>DimEjecutivo</c> si está configurada y el usuario aparece; si no, <c>UsuariosWeb.regional</c>.
/// Sin regional: <c>REGIONAL_NO_ASIGNADA</c>. Fuente viva configurada y caída: <c>REGIONAL_NO_DISPONIBLE</c>.
/// </summary>
public sealed class clsRegionalUsuarioServiceTests
{
    private readonly IClsDirectorioEjecutivos _directorio = Substitute.For<IClsDirectorioEjecutivos>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsRegionalUsuarioService Servicio() => new(_directorio, _usuarios, NullLogger<clsRegionalUsuarioService>.Instance);

    private static clsUsuarioWeb Usuario(string nombre, string? regional) =>
        clsUsuarioWeb.Crear(nombre, nombre, null, regional, DateTimeOffset.UtcNow).Valor;

    private void Directorio(params (string Usuario, string Regional)[] filas)
    {
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyDictionary<string, string>)c.Arg<IReadOnlyCollection<string>>()
                .Select(n => filas.FirstOrDefault(f => string.Equals(f.Usuario, n, StringComparison.OrdinalIgnoreCase)))
                .Where(f => f.Usuario is not null)
                .ToDictionary(f => f.Usuario, f => f.Regional, StringComparer.OrdinalIgnoreCase));
        _directorio.ListarNombresUsuarioAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyList<string>)[.. filas.Where(f => string.Equals(f.Regional, c.Arg<string>(), StringComparison.OrdinalIgnoreCase)).Select(f => f.Usuario)]);
    }

    [Fact]
    public async Task SinDirectorioLaRegionalSaleDeUsuariosWeb()
    {
        var resultado = await Servicio().ResolverAsync(Usuario("gloria", " Occidente "), Ct);

        resultado.Valor.Should().Be(new clsRegionalResuelta("Occidente", clsRegionalResuelta.FuenteUsuariosWeb));
        await _directorio.DidNotReceiveWithAnyArgs().ObtenerRegionalesAsync(default!, Ct);
    }

    [Fact]
    public async Task ElDirectorioMandaSobreUsuariosWeb()
    {
        Directorio(("gloria", "Norte"));

        var resultado = await Servicio().ResolverAsync(Usuario("gloria", "Occidente"), Ct);

        resultado.Valor.Should().Be(new clsRegionalResuelta("Norte", clsRegionalResuelta.FuenteDimEjecutivo));
    }

    [Fact]
    public async Task SiElUsuarioNoEstaEnElDirectorioSeUsaElRespaldo()
    {
        Directorio(("otro", "Norte"));

        var resultado = await Servicio().ResolverAsync(Usuario("gloria", "Occidente"), Ct);

        resultado.Valor.Fuente.Should().Be(clsRegionalResuelta.FuenteUsuariosWeb);
    }

    [Fact]
    public async Task SinRegionalEnNingunaFuenteEsNoAsignada()
    {
        Directorio();

        var resultado = await Servicio().ResolverAsync(Usuario("gloria", null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoAsignada);
    }

    [Fact]
    public async Task ConElDirectorioCaidoNoSeCaeAlRespaldo()
    {
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("red caída"));

        var resultado = await Servicio().ResolverAsync(Usuario("gloria", "Occidente"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
    }

    [Fact]
    public async Task LosMiembrosSinDirectorioSonLosDeUsuariosWeb()
    {
        var ana = Usuario("ana", "Occidente");
        _usuarios.ListarPorRegionalONombresAsync("Occidente", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([ana]);

        var resultado = await Servicio().ListarMiembrosAsync(" Occidente", Ct);

        resultado.Valor.Should().Equal(ana);
        await _usuarios.Received(1).ListarPorRegionalONombresAsync("Occidente", Arg.Is<IReadOnlyCollection<string>>(n => n.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LosMiembrosConDirectorioUsanLaRegionalEfectivaDeCadaUno()
    {
        // ana: el directorio la pone en Occidente aunque UsuariosWeb diga Norte; luis: UsuariosWeb dice Occidente pero el
        // directorio lo movió a Norte; marta: no está en el directorio y su respaldo es Occidente.
        Directorio(("ana", "OCCIDENTE"), ("luis", "Norte"));
        var ana = Usuario("ana", "Norte");
        var luis = Usuario("luis", "Occidente");
        var marta = Usuario("marta", "occidente");
        _usuarios.ListarPorRegionalONombresAsync("Occidente", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([ana, luis, marta]);

        var resultado = await Servicio().ListarMiembrosAsync("Occidente", Ct);

        resultado.Valor.Should().Equal(ana, marta);
        await _usuarios.Received(1).ListarPorRegionalONombresAsync(
            "Occidente", Arg.Is<IReadOnlyCollection<string>>(n => n.SequenceEqual(new[] { "ana" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LosMiembrosConElDirectorioCaidoEsNoDisponible()
    {
        _directorio.Configurado.Returns(true);
        _directorio.ListarNombresUsuarioAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var resultado = await Servicio().ListarMiembrosAsync("Occidente", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
    }

    [Fact]
    public async Task EsMiembroUsaLaRegionalEfectivaDelGerenteYDelUsuario()
    {
        // El gerente sigue en Norte en UsuariosWeb, pero DimEjecutivo lo pone en Occidente con ana.
        Directorio(("gerente", "Occidente"), ("ana", "Occidente"), ("luis", "Norte"));
        var gerente = Usuario("gerente", "Norte");
        var ana = Usuario("ana", "Norte");
        var luis = Usuario("luis", "Occidente");
        _usuarios.ListarPorRegionalONombresAsync("Occidente", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([gerente, ana, luis]);

        (await Servicio().EsMiembroDeMiRegionalAsync(gerente, ana.Id, Ct)).Valor.Should().BeTrue();
        (await Servicio().EsMiembroDeMiRegionalAsync(gerente, luis.Id, Ct)).Valor.Should().BeFalse();
    }

    [Fact]
    public async Task EsMiembroSinRegionalDelGerenteEsNoAsignada()
    {
        var resultado = await Servicio().EsMiembroDeMiRegionalAsync(Usuario("gerente", null), Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoAsignada);
    }

    [Fact]
    public async Task EsMiembroConElDirectorioCaidoEsNoDisponible()
    {
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var resultado = await Servicio().EsMiembroDeMiRegionalAsync(Usuario("gerente", "Occidente"), Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
    }
}
