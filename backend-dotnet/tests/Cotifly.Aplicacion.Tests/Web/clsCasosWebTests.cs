using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Aplicacion.Metricas.Services;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Web;

/// <summary>
/// Casos de uso de <c>/web/*</c> que revalidan el rol del token (además de la política del endpoint): administración de líneas
/// (solo gerente, auditada), métricas (alcance por rol y regional) y descarga del PDF (dueño o gerente de la regional).
/// </summary>
public sealed class clsCasosWebTests
{
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsMetricasRepository _metricas = Substitute.For<IClsMetricasRepository>();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();

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
    public async Task ElComercialVeSusMetricasYElGerenteLasDeSuRegional()
    {
        var comercial = Usuario(RolInterno.Comercial);
        var gerente = Usuario(RolInterno.GerenteComercial);
        _usuarios.ObtenerPorIdAsync(gerente.UsuarioId!.Value, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("gloria", "Gloria", null, "Occidente", DateTimeOffset.UtcNow).Valor);

        var alcanceComercial = (await Metricas(comercial).ResolverAlcanceAsync(Ct)).Valor;
        var alcanceGerente = (await Metricas(gerente).ResolverAlcanceAsync(Ct)).Valor;

        alcanceComercial.UsuarioId.Should().Be(comercial.UsuarioId);
        alcanceComercial.Regional.Should().BeNull();
        alcanceGerente.UsuarioId.Should().BeNull();
        alcanceGerente.Regional.Should().Be("Occidente");
    }

    [Fact]
    public async Task UnGerenteSinRegionalSoloVeSuPropiaFila()
    {
        var gerente = Usuario(RolInterno.GerenteComercial);
        _usuarios.ObtenerPorIdAsync(gerente.UsuarioId!.Value, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("gloria", "Gloria", null, null, DateTimeOffset.UtcNow).Valor);
        _metricas.ListarActividadAsync(Arg.Any<clsAlcanceMetricas>(), Arg.Any<CancellationToken>()).Returns([]);

        await Metricas(gerente).ObtenerActividadAsync(Ct);

        await _metricas.Received(1).ListarActividadAsync(
            Arg.Is<clsAlcanceMetricas>(a => a.UsuarioId == gerente.UsuarioId && a.Regional == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinSesionNoHayMetricas()
    {
        var resultado = await Metricas(new clsUsuarioFalso(null, null)).ObtenerAjustesMargenAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoAutenticado);
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
        var gerente = Usuario(RolInterno.GerenteComercial);
        var duenoId = Guid.NewGuid();
        CotizacionConPdf("PCN-DG-25092026-002", duenoId);
        _usuarios.ObtenerPorIdAsync(gerente.UsuarioId!.Value, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("gerente", null, null, regionalGerente, DateTimeOffset.UtcNow).Valor);
        _usuarios.ObtenerPorIdAsync(duenoId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("dueno", null, null, regionalDueno, DateTimeOffset.UtcNow).Valor);

        var resultado = await Descarga(gerente).ObtenerAsync("PCN-DG-25092026-002.pdf", Ct);

        resultado.EsExitoso.Should().Be(permitido);
        await _auditoria.Received(1).RegistrarAsync(clsDescargaPdfService.HerramientaAuditoria, Arg.Any<object>(), Arg.Any<object>(), permitido, Arg.Any<int>(), null);
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

    private clsMetricasService Metricas(IClsContextoUsuario usuario) => new(usuario, _usuarios, _metricas);

    private clsDescargaPdfService Descarga(IClsContextoUsuario usuario) => new(usuario, _cotizaciones, _usuarios, _almacen, _auditoria);

    private clsCotizacion CotizacionConPdf(string folio, Guid dueno)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = dueno, PdfPath = $"/data/pdfs/{folio}.pdf" }).Valor;
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
