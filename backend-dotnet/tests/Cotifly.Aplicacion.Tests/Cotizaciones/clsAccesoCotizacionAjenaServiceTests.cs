using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// HU-034.1 (#201, parte A): <see cref="clsAccesoCotizacionAjenaService"/>, la cotización ajena que puede consultar en solo lectura el
/// gerente de la regional del dueño o el director, con la regla real de <see cref="clsRegionalUsuarioService"/> (fail-closed) y auditada.
/// </summary>
public sealed class clsAccesoCotizacionAjenaServiceTests
{
    private const string Herramienta = "consultar_licitacion";
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsDirectorioEjecutivos _directorio = Substitute.For<IClsDirectorioEjecutivos>();
    private readonly List<clsUsuarioWeb> _registrados = [];

    public clsAccesoCotizacionAjenaServiceTests()
    {
        _usuarios.ListarPorRegionalONombresAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyList<clsUsuarioWeb>)[.. _registrados.Where(u =>
                string.Equals(u.Regional?.Trim(), c.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase))]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Occidente", "Occidente", true)]
    [InlineData("Norte", "Occidente", false)]
    [InlineData(null, "Occidente", false)]
    public async Task ElGerenteSoloObtieneLasDeSuRegionalYCadaIntentoQuedaAuditado(string? regionalGerente, string regionalDueno, bool permitido)
    {
        var (gerente, _) = Registrar("gerente", regionalGerente, RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("dueno", regionalDueno, RolInterno.Comercial);
        var cotizacion = Cotizacion("LIC-1", dueno.Id);

        var resultado = await Acceso(gerente).ObtenerAsync(gerente.UsuarioId!.Value, "LIC-1", Herramienta, Ct);

        resultado.Should().BeSameAs(permitido ? cotizacion : null);
        await _auditoria.Received(1).RegistrarAsync(Herramienta, Arg.Any<object>(), Arg.Any<object>(), permitido, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task ElGerenteSinRegionalQuedaAuditadoConElMotivo()
    {
        var (gerente, _) = Registrar("gerente", null, RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("dueno", "Occidente", RolInterno.Comercial);
        Cotizacion("LIC-2", dueno.Id);

        var resultado = await Acceso(gerente).ObtenerAsync(gerente.UsuarioId!.Value, "LIC-2", Herramienta, Ct);

        resultado.Should().BeNull();
        await _auditoria.Received(1).RegistrarAsync(
            Herramienta,
            Arg.Is<object>(e => e.ToString()!.Contains("LIC-2", StringComparison.Ordinal)),
            Arg.Is<object>(s => s.ToString()!.Contains(clsCodigosAplicacion.RegionalNoAsignada, StringComparison.Ordinal)),
            false,
            Arg.Any<int>(),
            null);
    }

    [Fact]
    public async Task SiLaFuenteDeLaRegionalCaeElGerenteNoObtieneNada()
    {
        var (gerente, _) = Registrar("gerente.occ", "Occidente", RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("comercial", "Occidente", RolInterno.Comercial);
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        _directorio.ListarNombresUsuarioAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        Cotizacion("LIC-3", dueno.Id);

        var resultado = await Acceso(gerente).ObtenerAsync(gerente.UsuarioId!.Value, "LIC-3", Herramienta, Ct);

        resultado.Should().BeNull();
        await _auditoria.Received(1).RegistrarAsync(
            Herramienta,
            Arg.Any<object>(),
            Arg.Is<object>(s => s.ToString()!.Contains(clsCodigosAplicacion.RegionalNoDisponible, StringComparison.Ordinal)),
            false,
            Arg.Any<int>(),
            null);
    }

    [Fact]
    public async Task ElDirectorObtieneLaDeCualquierRegional()
    {
        var (director, _) = Registrar("director", null, RolInterno.Director);
        var (_, dueno) = Registrar("comercial.norte", "Norte", RolInterno.Comercial);
        var cotizacion = Cotizacion("LIC-4", dueno.Id);

        var resultado = await Acceso(director).ObtenerAsync(director.UsuarioId!.Value, "LIC-4", Herramienta, Ct);

        resultado.Should().BeSameAs(cotizacion);
        await _auditoria.Received(1).RegistrarAsync(
            Herramienta, Arg.Is<object>(e => e.ToString()!.Contains("rol = director", StringComparison.Ordinal)), Arg.Any<object>(), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task UnComercialNiCargaLaCotizacionNiQuedaAuditado()
    {
        var (comercial, _) = Registrar("luis", "Occidente", RolInterno.Comercial);
        var (_, dueno) = Registrar("ana", "Occidente", RolInterno.Comercial);
        Cotizacion("LIC-5", dueno.Id);

        var resultado = await Acceso(comercial).ObtenerAsync(comercial.UsuarioId!.Value, "LIC-5", Herramienta, Ct);

        resultado.Should().BeNull();
        await _cotizaciones.DidNotReceiveWithAnyArgs().ObtenerPorFolioAsync(default!, Ct);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task SoloSirveParaElUsuarioDeLaSesionYNoParaLasPropiasNiLasInexistentes()
    {
        var (director, usuarioDirector) = Registrar("director", null, RolInterno.Director);
        var (_, dueno) = Registrar("ana", "Occidente", RolInterno.Comercial);
        Cotizacion("LIC-6", dueno.Id);
        Cotizacion("LIC-7", usuarioDirector.Id);

        var deOtro = await Acceso(director).ObtenerAsync(Guid.NewGuid(), "LIC-6", Herramienta, Ct);
        var propia = await Acceso(director).ObtenerAsync(usuarioDirector.Id, "LIC-7", Herramienta, Ct);
        var inexistente = await Acceso(director).ObtenerAsync(usuarioDirector.Id, "LIC-X", Herramienta, Ct);
        var vacia = await Acceso(director).ObtenerAsync(usuarioDirector.Id, " ", Herramienta, Ct);

        new[] { deOtro, propia, inexistente, vacia }.Should().AllSatisfy(c => c.Should().BeNull());
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default!, default!, default, default, default);
    }

    private clsAccesoCotizacionAjenaService Acceso(IClsContextoUsuario usuario) =>
        new(usuario, _cotizaciones, _usuarios, new clsRegionalUsuarioService(_directorio, _usuarios, NullLogger<clsRegionalUsuarioService>.Instance), _auditoria);

    private (clsUsuarioFalso Sesion, clsUsuarioWeb Usuario) Registrar(string nombre, string? regional, RolInterno rol)
    {
        var usuarioWeb = clsUsuarioWeb.Crear(nombre, nombre, null, regional, DateTimeOffset.UtcNow).Valor;
        _registrados.Add(usuarioWeb);
        _usuarios.ObtenerPorIdAsync(usuarioWeb.Id, Arg.Any<CancellationToken>()).Returns(usuarioWeb);
        return (new clsUsuarioFalso(usuarioWeb.Id, rol), usuarioWeb);
    }

    private clsCotizacionSolicitud Cotizacion(string folio, Guid dueno)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = dueno }).Valor;
        _cotizaciones.ObtenerPorFolioAsync(folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        return cotizacion;
    }

    private sealed class clsUsuarioFalso(Guid? usuarioId, RolInterno? rol) : IClsContextoUsuario
    {
        public bool EstaAutenticado => UsuarioId is not null;

        public Guid? UsuarioId { get; } = usuarioId;

        public string? NombreUsuario => UsuarioId is null ? null : "usuario.prueba";

        public RolInterno? Rol { get; } = rol;
    }
}
