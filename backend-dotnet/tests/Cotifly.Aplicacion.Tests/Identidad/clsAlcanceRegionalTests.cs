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
/// HU-037.3 (#211, ADR-016 §11): el alcance de regionales por rol, centralizado en <see cref="IClsRegionalUsuario"/>. El director ve todas
/// (o la que elige); el gerente solo la suya (otra: <c>REGIONAL_NO_PERMITIDA</c>) y sigue fallando cerrado sin regional; el comercial no
/// tiene alcance. Las regionales del selector salen de la misma regla de la regional efectiva.
/// </summary>
public sealed class clsAlcanceRegionalTests
{
    private readonly IClsDirectorioEjecutivos _directorio = Substitute.For<IClsDirectorioEjecutivos>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly List<clsUsuarioWeb> _todos = [];

    public clsAlcanceRegionalTests()
    {
        _usuarios.ListarTodosAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<clsUsuarioWeb>)[.. _todos]);
        _usuarios.ListarPorRegionalONombresAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyList<clsUsuarioWeb>)[.. _todos.Where(u =>
                string.Equals(u.Regional?.Trim(), c.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase)
                || c.ArgAt<IReadOnlyCollection<string>>(1).Contains(u.NombreUsuario ?? string.Empty, StringComparer.OrdinalIgnoreCase))]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsRegionalUsuarioService Servicio() => new(_directorio, _usuarios, NullLogger<clsRegionalUsuarioService>.Instance);

    private clsUsuarioWeb Usuario(string nombre, string? regional)
    {
        var usuario = clsUsuarioWeb.Crear(nombre, nombre, null, regional, DateTimeOffset.UtcNow).Valor;
        _todos.Add(usuario);
        return usuario;
    }

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
    public async Task ElDirectorSinRegionalVeATodosLosUsuariosSinConsultarSuPropiaRegional()
    {
        var director = Usuario("dora", null);
        var ana = Usuario("ana", "Occidente");
        var marta = Usuario("marta", "Norte");
        var sinRegional = Usuario("pedro", null);

        var alcance = (await Servicio().ResolverAlcanceAsync(director, RolInterno.Director, null, Ct)).Valor;

        alcance.Todas.Should().BeTrue();
        alcance.Regional.Should().BeNull();
        alcance.Fuente.Should().BeNull();
        alcance.Miembros.Should().BeEquivalentTo([director, ana, marta, sinRegional]);
        await _directorio.DidNotReceiveWithAnyArgs().ObtenerRegionalesAsync(default!, Ct);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnaRegionalVaciaEquivaleANoPedirla(string regional)
    {
        var director = Usuario("dora", null);

        var alcance = (await Servicio().ResolverAlcanceAsync(director, RolInterno.Director, regional, Ct)).Valor;

        alcance.Todas.Should().BeTrue();
    }

    [Fact]
    public async Task ElDirectorQueEligeUnaRegionalVeSoloSusMiembrosConLaRegionalEfectiva()
    {
        var director = Usuario("dora", null);
        var ana = Usuario("ana", "Occidente");
        var luis = Usuario("luis", "Norte");
        Usuario("marta", "Norte");
        // DimEjecutivo manda: luis pasó a Occidente aunque UsuariosWeb siga en Norte.
        Directorio(("luis", "Occidente"), ("marta", "Norte"));

        var alcance = (await Servicio().ResolverAlcanceAsync(director, RolInterno.Director, " occidente ", Ct)).Valor;

        alcance.Regional.Should().Be("occidente");
        alcance.Fuente.Should().BeNull("la regional del director no sale de su usuario");
        alcance.Miembros.Should().BeEquivalentTo([ana, luis]);
    }

    [Fact]
    public async Task ElDirectorQueEligeUnaRegionalConLaFuenteCaidaRecibe503()
    {
        var director = Usuario("dora", null);
        _directorio.Configurado.Returns(true);
        _directorio.ListarNombresUsuarioAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var resultado = await Servicio().ResolverAlcanceAsync(director, RolInterno.Director, "Norte", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Occidente")]
    [InlineData("  OCCIDENTE ")]
    public async Task ElGerenteVeSuRegionalSinPedirlaOPidiendolaConOtraEscritura(string? regional)
    {
        var gloria = Usuario("gloria", "Occidente");
        var ana = Usuario("ana", "Occidente");
        Usuario("marta", "Norte");

        var alcance = (await Servicio().ResolverAlcanceAsync(gloria, RolInterno.GerenteComercial, regional, Ct)).Valor;

        alcance.Regional.Should().Be("Occidente");
        alcance.Fuente.Should().Be(clsRegionalResuelta.FuenteUsuariosWeb);
        alcance.Miembros.Should().BeEquivalentTo([gloria, ana]);
    }

    [Fact]
    public async Task ElGerenteQuePideOtraRegionalRecibeRegionalNoPermitidaSinListarMiembros()
    {
        var gloria = Usuario("gloria", "Occidente");
        Usuario("marta", "Norte");

        var resultado = await Servicio().ResolverAlcanceAsync(gloria, RolInterno.GerenteComercial, "Norte", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoPermitida);
        await _usuarios.DidNotReceiveWithAnyArgs().ListarPorRegionalONombresAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task ElGerenteSinRegionalSigueFallandoCerradoAunquePidaUna()
    {
        var gloria = Usuario("gloria", null);
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var caida = await Servicio().ResolverAlcanceAsync(gloria, RolInterno.GerenteComercial, "Norte", Ct);
        _directorio.Configurado.Returns(false);
        var sinRegional = await Servicio().ResolverAlcanceAsync(gloria, RolInterno.GerenteComercial, null, Ct);

        caida.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
        sinRegional.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoAsignada);
        await _usuarios.DidNotReceiveWithAnyArgs().ListarTodosAsync(Ct);
    }

    [Fact]
    public async Task UnComercialNoTieneAlcanceRegional()
    {
        var ana = Usuario("ana", "Occidente");

        var resultado = await Servicio().ResolverAlcanceAsync(ana, RolInterno.Comercial, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
    }

    [Fact]
    public async Task ElDirectorPuedeVerLoDeCualquierRegionalSinConsultarLaFuente()
    {
        var director = Usuario("dora", null);
        var marta = Usuario("marta", "Norte");
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var resultado = await Servicio().PuedeVerDeAsync(director, RolInterno.Director, marta.Id, Ct);

        resultado.Valor.Should().BeTrue();
    }

    [Theory]
    [InlineData("Occidente", true)]
    [InlineData("Norte", false)]
    public async Task ElGerentePuedeVerSoloLoDeSuRegional(string regionalDueno, bool permitido)
    {
        var gloria = Usuario("gloria", "Occidente");
        var dueno = Usuario("dueno", regionalDueno);

        var resultado = await Servicio().PuedeVerDeAsync(gloria, RolInterno.GerenteComercial, dueno.Id, Ct);

        resultado.Valor.Should().Be(permitido);
    }

    [Fact]
    public async Task UnComercialNoPuedeVerLoAjeno()
    {
        var ana = Usuario("ana", "Occidente");
        var luis = Usuario("luis", "Occidente");

        var resultado = await Servicio().PuedeVerDeAsync(ana, RolInterno.Comercial, luis.Id, Ct);

        resultado.Valor.Should().BeFalse();
    }

    [Fact]
    public async Task ElDirectorListaLasRegionalesEfectivasDeTodosSinRepetirYOrdenadas()
    {
        var director = Usuario("dora", null);
        Usuario("ana", "Occidente");
        Usuario("luis", " occidente ");
        Usuario("marta", "Norte");
        Usuario("pedro", "Sur");
        Usuario("sin.regional", null);
        // DimEjecutivo manda: pedro está en Centro (y Sur ya no tiene a nadie).
        Directorio(("pedro", "Centro"));

        var regionales = (await Servicio().ListarRegionalesAsync(director, RolInterno.Director, Ct)).Valor;

        regionales.Should().Equal("Centro", "Norte", "Occidente");
    }

    [Fact]
    public async Task ConLaFuenteCaidaElDirectorRecibe503AlListarRegionales()
    {
        var director = Usuario("dora", null);
        Usuario("ana", "Occidente");
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var resultado = await Servicio().ListarRegionalesAsync(director, RolInterno.Director, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
    }

    [Fact]
    public async Task ElGerenteSoloListaLaSuyaYElComercialNinguna()
    {
        var gloria = Usuario("gloria", "Occidente");
        Usuario("marta", "Norte");
        var ines = Usuario("ines", null);
        var ana = Usuario("ana", "Occidente");

        (await Servicio().ListarRegionalesAsync(gloria, RolInterno.GerenteComercial, Ct)).Valor.Should().Equal("Occidente");
        (await Servicio().ListarRegionalesAsync(ines, RolInterno.GerenteComercial, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoAsignada);
        (await Servicio().ListarRegionalesAsync(ana, RolInterno.Comercial, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
    }
}
