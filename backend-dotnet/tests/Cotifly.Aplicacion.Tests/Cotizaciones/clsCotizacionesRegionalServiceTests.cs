using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// HU-016.1 (#154): listado regional del gerente comercial. Rol revalidado, regional resuelta en el servidor (409/503 sin consultar
/// cotizaciones), solo los miembros de la regional, misma forma que "Mis cotizaciones" más el comercial dueño, mismo tope.
/// </summary>
public sealed class clsCotizacionesRegionalServiceTests
{
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsRegionalUsuario _regionales = Substitute.For<IClsRegionalUsuario>();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SinSesionNoHayListado()
    {
        var resultado = await Servicio(new clsUsuarioFalso(null, null)).ListarAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoAutenticado);
    }

    [Fact]
    public async Task UnComercialNoVeLaRegionalAunqueLlegueAlCasoDeUso()
    {
        var resultado = await Servicio(new clsUsuarioFalso(Guid.NewGuid(), RolInterno.Comercial)).ListarAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
        await _regionales.DidNotReceiveWithAnyArgs().ResolverAsync(default!, Ct);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ListarPorUsuariosAsync(default!, default, Ct);
    }

    [Fact]
    public async Task UnGerenteQueYaNoExisteEsNoAutenticado()
    {
        var resultado = await Servicio(new clsUsuarioFalso(Guid.NewGuid(), RolInterno.GerenteComercial)).ListarAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoAutenticado);
    }

    [Theory]
    [InlineData(clsCodigosAplicacion.RegionalNoAsignada)]
    [InlineData(clsCodigosAplicacion.RegionalNoDisponible)]
    public async Task SinRegionalElGerenteRecibeElErrorYNoSeConsultanCotizaciones(string codigo)
    {
        var gloria = UsuarioWeb("gloria", "Gloria");
        _usuarios.ObtenerPorIdAsync(gloria.Id, Arg.Any<CancellationToken>()).Returns(gloria);
        _regionales.ResolverAsync(gloria, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsRegionalResuelta>.Fallo(new clsErrorDominio(codigo, "sin regional")));

        var resultado = await Servicio(Gerente(gloria)).ListarAsync(Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ListarPorUsuariosAsync(default!, default, Ct);
    }

    [Fact]
    public async Task SiLaFuenteDeLosMiembrosFallaResponde503YNoSeConsultanCotizaciones()
    {
        var gloria = PrepararRegional([]);
        _regionales.ListarMiembrosAsync("Occidente", Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsUsuarioWeb>>.Fallo(new clsErrorDominio(clsCodigosAplicacion.RegionalNoDisponible, "caída")));

        var resultado = await Servicio(Gerente(gloria)).ListarAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ListarPorUsuariosAsync(default!, default, Ct);
    }

    [Fact]
    public async Task ElGerenteVeLasDeLosMiembrosDeSuRegionalConElComercialDuenoYLaFormaDeMisCotizaciones()
    {
        var ana = UsuarioWeb("ana", "Ana Pérez");
        var luis = UsuarioWeb("luis", null);
        var gloria = PrepararRegional([ana, luis]);
        var deLuis = Cotizacion("PCN-LU-01102026-001", luis.Id);
        var deAna = Cotizacion("PCN-AP-30092026-001", ana.Id);
        _cotizaciones.ListarPorUsuariosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), 100, Arg.Any<CancellationToken>())
            .Returns([new clsCotizacionConLinea(deLuis, clsCotizacionConLinea.Varias, 0.18m, OrigenMargen.SapTm),
                      new clsCotizacionConLinea(deAna, "Nacional", 0.15m, OrigenMargen.PoliticaLinea)]);

        var lista = (await Servicio(Gerente(gloria)).ListarAsync(Ct)).Valor;

        await _cotizaciones.Received(1).ListarPorUsuariosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Order().SequenceEqual(new[] { ana.Id, luis.Id, gloria.Id }.Order())),
            IClsCotizacionRepository.LimitePorDefecto, Arg.Any<CancellationToken>());
        (lista.Regional, lista.FuenteRegional, lista.Total).Should().Be(("Occidente", clsRegionalResuelta.FuenteUsuariosWeb, 2));
        lista.Cotizaciones.Select(c => c.Folio).Should().Equal("PCN-LU-01102026-001", "PCN-AP-30092026-001");
        lista.Cotizaciones[0].Comercial.Should().Be(new clsComercialCotizacion(luis.Id, "luis", null));
        lista.Cotizaciones[1].Comercial.Should().Be(new clsComercialCotizacion(ana.Id, "ana", "Ana Pérez"));
        // Misma forma que "Mis cotizaciones": la fila es el resumen de siempre con el comercial agregado.
        lista.Cotizaciones[0].Should().Be(
            clsCotizacionResumen.Desde(deLuis, "Varias", "https://api.cotifly/", 0.18m, OrigenMargen.SapTm) with
            {
                Comercial = new clsComercialCotizacion(luis.Id, "luis", null),
            });
        lista.Cotizaciones[1].PdfUrl.Should().Be("https://api.cotifly/pdfs/PCN-AP-30092026-001.pdf");
    }

    [Fact]
    public async Task NuncaDevuelveUnaFilaDeAlguienFueraDeLaRegional()
    {
        var ana = UsuarioWeb("ana", "Ana");
        var gloria = PrepararRegional([ana]);
        _cotizaciones.ListarPorUsuariosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new clsCotizacionConLinea(Cotizacion("PCN-MA-01102026-001", Guid.NewGuid()), "Nacional"),
                      new clsCotizacionConLinea(Cotizacion("PCN-AN-01102026-001", ana.Id), "Nacional")]);

        var lista = (await Servicio(Gerente(gloria)).ListarAsync(Ct)).Valor;

        lista.Cotizaciones.Should().ContainSingle().Which.Folio.Should().Be("PCN-AN-01102026-001");
        lista.Total.Should().Be(1);
    }

    [Fact]
    public async Task UnaRegionalSinCotizacionesDevuelveUnaListaVacia()
    {
        var gloria = PrepararRegional([]);
        _cotizaciones.ListarPorUsuariosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var lista = (await Servicio(Gerente(gloria)).ListarAsync(Ct)).Valor;

        (lista.Total, lista.Cotizaciones.Count).Should().Be((0, 0));
    }

    /// <summary>El gerente "gloria" de la regional "Occidente", cuyos miembros son ella y <paramref name="comerciales"/>.</summary>
    private clsUsuarioWeb PrepararRegional(IReadOnlyList<clsUsuarioWeb> comerciales)
    {
        var gloria = UsuarioWeb("gloria", "Gloria");
        _usuarios.ObtenerPorIdAsync(gloria.Id, Arg.Any<CancellationToken>()).Returns(gloria);
        _regionales.ResolverAsync(gloria, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsRegionalResuelta>.Exito(new clsRegionalResuelta("Occidente", clsRegionalResuelta.FuenteUsuariosWeb)));
        _regionales.ListarMiembrosAsync("Occidente", Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsUsuarioWeb>>.Exito([.. comerciales, gloria]));
        return gloria;
    }

    private static clsCotizacionSolicitud Cotizacion(string folio, Guid usuarioId)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            UsuarioId = usuarioId,
            ClienteNombre = "Cliente SA",
            PdfPath = $"/data/pdfs/{folio}.pdf",
        }).Valor;
        cotizacion.RegistrarCotizacionChat(1, 100m, 0.15m, 0.15m, OrigenMargen.PoliticaLinea, "markup", 115m);
        return cotizacion;
    }

    private static clsUsuarioWeb UsuarioWeb(string nombre, string? nombreCompleto) =>
        clsUsuarioWeb.Crear(nombre, nombreCompleto, null, "Occidente", DateTimeOffset.UtcNow).Valor;

    private static clsUsuarioFalso Gerente(clsUsuarioWeb usuario) => new(usuario.Id, RolInterno.GerenteComercial);

    private clsCotizacionesRegionalService Servicio(IClsContextoUsuario usuario) =>
        new(usuario, _usuarios, _regionales, _cotizaciones, Options.Create(new clsOpcionesPdf { BaseUrl = "https://api.cotifly/" }));

    private sealed class clsUsuarioFalso(Guid? usuarioId, RolInterno? rol) : IClsContextoUsuario
    {
        public bool EstaAutenticado => UsuarioId is not null;

        public Guid? UsuarioId { get; } = usuarioId;

        public string? NombreUsuario => UsuarioId is null ? null : "usuario.prueba";

        public RolInterno? Rol { get; } = rol;
    }
}
