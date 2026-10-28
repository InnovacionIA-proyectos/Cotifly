using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Web;

/// <summary>
/// Casos de uso de <c>/web/*</c> que revalidan el rol del token (además de la política del endpoint): administración de líneas
/// (solo gerente, auditada) y descarga del PDF (dueño o gerente de la regional).
/// </summary>
public sealed class clsCasosWebTests
{
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsDirectorioEjecutivos _directorio = Substitute.For<IClsDirectorioEjecutivos>();
    private readonly List<clsUsuarioWeb> _registrados = [];

    public clsCasosWebTests()
    {
        // UsuariosWeb en memoria para la regla real de regional efectiva (clsRegionalUsuarioService).
        _usuarios.ListarPorRegionalONombresAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyList<clsUsuarioWeb>)[.. _registrados.Where(u =>
                string.Equals(u.Regional?.Trim(), c.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase)
                || (u.NombreUsuario is { } n && c.ArgAt<IReadOnlyCollection<string>>(1).Contains(n, StringComparer.OrdinalIgnoreCase)))]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElGerenteEditaLaLineaYQuedaAuditadoQuienAntesYDespues()
    {
        var gerente = Usuario(RolInterno.GerenteComercial);
        var politica = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(politica);

        var resultado = await Admin(gerente).ActualizarAsync(3, new clsCambioLineaNegocio(0.2m, "markup", false), Ct);

        resultado.Valor.Should().BeEquivalentTo(new { LineaNegocio = "Nacional", Margen = 0.2m, MetodoCalculo = "markup", IsActive = false });
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(
            clsLineasNegocioAdminService.HerramientaAuditoria,
            Arg.Is<object>(e => e.ToString()!.Contains(gerente.UsuarioId!.Value.ToString(), StringComparison.Ordinal)),
            Arg.Is<object>(s => s.ToString()!.Contains("antes", StringComparison.Ordinal) && s.ToString()!.Contains("despues", StringComparison.Ordinal)),
            true,
            Arg.Any<int>(),
            null);
    }

    [Fact]
    public async Task ElDirectorAdministraLasLineasComoElGerente()
    {
        var director = Usuario(RolInterno.Director);
        var politica = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(politica);

        var listar = await Admin(director).ListarAsync(Ct);
        var editar = await Admin(director).ActualizarAsync(3, new clsCambioLineaNegocio(0.25m, "margen", true), Ct);

        listar.EsExitoso.Should().BeTrue();
        editar.Valor.Margen.Should().Be(0.25m);
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnComercialNoAdministraLineasAunqueLlegueAlCasoDeUso()
    {
        var comercial = Usuario(RolInterno.Comercial);

        var listar = await Admin(comercial).ListarAsync(Ct);
        var editar = await Admin(comercial).ActualizarAsync(3, new clsCambioLineaNegocio(0.2m, "margen", true), Ct);

        listar.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
        editar.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
        await _politicas.DidNotReceiveWithAnyArgs().ObtenerPorIdAsync(default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsLineasNegocioAdminService.HerramientaAuditoria, Arg.Any<object>(), Arg.Any<object>(), false, Arg.Any<int>(), null);
    }

    [Theory]
    [InlineData(null, "margen", true)]
    [InlineData(0.2, null, true)]
    [InlineData(0.2, "margen", null)]
    [InlineData(-0.01, "margen", true)]
    [InlineData(0.991, "markup", true)]
    [InlineData(0.2, "otro", true)]
    public async Task UnCambioInvalidoEsSolicitudInvalidaYNoSeGuarda(double? margen, string? metodo, bool? activa)
    {
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Nacional", 0.15m).Valor);

        var resultado = await Admin(Usuario(RolInterno.GerenteComercial))
            .ActualizarAsync(3, new clsCambioLineaNegocio((decimal?)margen, metodo, activa), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        await _unidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task EditarUnaLineaInexistenteEsNoEncontrado()
    {
        var resultado = await Admin(Usuario(RolInterno.GerenteComercial)).ActualizarAsync(99, new clsCambioLineaNegocio(0.2m, "margen", true), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
    }

    [Fact]
    public async Task ElDuenoDescargaSuPdfYOtroComercialRecibeNoEncontrado()
    {
        var dueno = Usuario(RolInterno.Comercial);
        var otro = Usuario(RolInterno.Comercial);
        var cotizacion = CotizacionConPdf("PCN-DG-25092026-001", dueno.UsuarioId!.Value);

        var propia = await Descarga(dueno).ObtenerAsync("PCN-DG-25092026-001.pdf", Ct);
        var ajena = await Descarga(otro).ObtenerAsync("PCN-DG-25092026-001.pdf", Ct);

        propia.Valor.Contenido.Should().Equal(1, 2, 3);
        ajena.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        ajena.Error.Mensaje.Should().Be("Cotización no encontrada o no es tuya.");
        await _auditoria.Received(1).RegistrarAsync(clsDescargaPdfService.HerramientaAuditoria, Arg.Any<object>(), Arg.Any<object>(), false, Arg.Any<int>(), null);
        cotizacion.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Occidente", "Occidente", true)]
    [InlineData("occidente ", "Occidente", true)]
    [InlineData("Norte", "Occidente", false)]
    [InlineData("Norte", null, false)]
    [InlineData(null, "Occidente", false)]
    [InlineData(null, null, false)]
    public async Task ElGerenteDescargaSoloPdfDeSuRegionalCuandoLaConoce(string? regionalGerente, string? regionalDueno, bool permitido)
    {
        var (gerente, _) = Registrar("gerente", regionalGerente, RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("dueno", regionalDueno, RolInterno.Comercial);
        CotizacionConPdf("PCN-DG-25092026-002", dueno.Id);

        var resultado = await Descarga(gerente).ObtenerAsync("PCN-DG-25092026-002.pdf", Ct);

        resultado.EsExitoso.Should().Be(permitido);
        if (!permitido)
        {
            resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        }

        await _auditoria.Received(1).RegistrarAsync(clsDescargaPdfService.HerramientaAuditoria, Arg.Any<object>(), Arg.Any<object>(), permitido, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task ElGerenteDescargaSiDimEjecutivoPoneAlDuenoEnSuRegionalAunqueUsuariosWebEsteDesactualizado()
    {
        // El comercial pasó de Norte a Occidente y no ha vuelto a entrar: UsuariosWeb.regional sigue en Norte.
        var (gerente, _) = Registrar("gerente.occ", "Occidente", RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("comercial", "Norte", RolInterno.Comercial);
        Directorio(("gerente.occ", "Occidente"), ("comercial", "Occidente"));
        CotizacionConPdf("PCN-DG-25092026-003", dueno.Id);

        var resultado = await Descarga(gerente).ObtenerAsync("PCN-DG-25092026-003.pdf", Ct);

        resultado.Valor.Contenido.Should().Equal(1, 2, 3);
        await _auditoria.Received(1).RegistrarAsync(clsDescargaPdfService.HerramientaAuditoria, Arg.Any<object>(), Arg.Any<object>(), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task ElDirectorDescargaElPdfDeCualquierRegionalAunqueLaFuenteEsteCaidaYQuedaAuditado()
    {
        // HU-037.3 (#211): el director no tiene alcance regional que comprobar.
        var (director, _) = Registrar("director", null, RolInterno.Director);
        var (_, dueno) = Registrar("comercial.norte", "Norte", RolInterno.Comercial);
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        CotizacionConPdf("PCN-DG-25092026-009", dueno.Id);

        var resultado = await Descarga(director).ObtenerAsync("PCN-DG-25092026-009.pdf", Ct);

        resultado.Valor.Contenido.Should().Equal(1, 2, 3);
        await _auditoria.Received(1).RegistrarAsync(
            clsDescargaPdfService.HerramientaAuditoria,
            Arg.Is<object>(e => e.ToString()!.Contains("rol = director", StringComparison.Ordinal)),
            Arg.Any<object>(), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task ElGerenteAnteriorNoDescargaAunqueUsuariosWebSigaDiciendoSuRegional()
    {
        var (gerenteAnterior, _) = Registrar("gerente.norte", "Norte", RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("comercial", "Norte", RolInterno.Comercial);
        Directorio(("gerente.norte", "Norte"), ("comercial", "Occidente"));
        CotizacionConPdf("PCN-DG-25092026-004", dueno.Id);

        var resultado = await Descarga(gerenteAnterior).ObtenerAsync("PCN-DG-25092026-004.pdf", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        await _almacen.DidNotReceiveWithAnyArgs().LeerPorReferenciaAsync(default!, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsDescargaPdfService.HerramientaAuditoria, Arg.Any<object>(), Arg.Any<object>(), false, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task SiLaFuenteDeLaRegionalCaeElGerenteRecibeNoEncontrado()
    {
        var (gerente, _) = Registrar("gerente.occ", "Occidente", RolInterno.GerenteComercial);
        var (_, dueno) = Registrar("comercial", "Occidente", RolInterno.Comercial);
        DirectorioCaido();
        CotizacionConPdf("PCN-DG-25092026-005", dueno.Id);

        var resultado = await Descarga(gerente).ObtenerAsync("PCN-DG-25092026-005.pdf", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        await _almacen.DidNotReceiveWithAnyArgs().LeerPorReferenciaAsync(default!, Ct);
        await _auditoria.Received(1).RegistrarAsync(
            clsDescargaPdfService.HerramientaAuditoria,
            Arg.Any<object>(),
            Arg.Is<object>(s => s.ToString()!.Contains(clsCodigosAplicacion.RegionalNoDisponible, StringComparison.Ordinal)),
            false,
            Arg.Any<int>(),
            null);
    }

    [Fact]
    public async Task ElDuenoDescargaSuPdfAunqueLaFuenteDeLaRegionalEsteCaida()
    {
        var (comercial, dueno) = Registrar("comercial", "Occidente", RolInterno.Comercial);
        DirectorioCaido();
        CotizacionConPdf("PCN-DG-25092026-006", dueno.Id);

        var resultado = await Descarga(comercial).ObtenerAsync("PCN-DG-25092026-006.pdf", Ct);

        resultado.Valor.Contenido.Should().Equal(1, 2, 3);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default!, default!, default, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PCN-1")]
    [InlineData("../PCN-1.pdf")]
    [InlineData("PCN-1.PDF")]
    [InlineData("otro.pdf")]
    public async Task UnNombreInvalidoOSinCotizacionEsNoEncontrado(string? archivo)
    {
        var resultado = await Descarga(Usuario(RolInterno.Comercial)).ObtenerAsync(archivo, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
    }

    private static clsUsuarioFalso Usuario(RolInterno rol) => new(Guid.NewGuid(), rol);

    private clsLineasNegocioAdminService Admin(IClsContextoUsuario usuario) => new(usuario, _politicas, _unidadTrabajo, _auditoria);

    private clsDescargaPdfService Descarga(IClsContextoUsuario usuario) =>
        new(usuario, _cotizaciones, _usuarios, new clsRegionalUsuarioService(_directorio, _usuarios, NullLogger<clsRegionalUsuarioService>.Instance), _almacen, _auditoria);

    /// <summary>Registra un usuario en UsuariosWeb (en memoria) y devuelve su sesión.</summary>
    private (clsUsuarioFalso Sesion, clsUsuarioWeb Usuario) Registrar(string nombre, string? regional, RolInterno rol)
    {
        var usuarioWeb = clsUsuarioWeb.Crear(nombre, nombre, null, regional, DateTimeOffset.UtcNow).Valor;
        _registrados.Add(usuarioWeb);
        _usuarios.ObtenerPorIdAsync(usuarioWeb.Id, Arg.Any<CancellationToken>()).Returns(usuarioWeb);
        return (new clsUsuarioFalso(usuarioWeb.Id, rol), usuarioWeb);
    }

    /// <summary>DimEjecutivo configurada con estas filas (usuario, regional).</summary>
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

    /// <summary>DimEjecutivo configurada pero caída.</summary>
    private void DirectorioCaido()
    {
        _directorio.Configurado.Returns(true);
        _directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DimEjecutivo caída"));
        _directorio.ListarNombresUsuarioAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DimEjecutivo caída"));
    }

    private clsCotizacionSolicitud CotizacionConPdf(string folio, Guid dueno)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = dueno, PdfPath = $"/data/pdfs/{folio}.pdf" }).Valor;
        _cotizaciones.ObtenerPorFolioAsync(folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _almacen.LeerPorReferenciaAsync(cotizacion.PdfPath, Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
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
