using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// HU-027.1 (#179): detalle por folio. Composición de las proyecciones existentes (resumen, costeo y captura) sin reglas nuevas, acceso
/// del dueño y del gerente de su regional (solo consulta, auditado, fail-closed) y 404 para cualquier otro; nunca consulta Nexo.
/// </summary>
public sealed class clsDetalleCotizacionServiceTests
{
    private const string BaseUrl = "https://api.cotifly/";
    private const int Linea = 1;

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsRegionalUsuario _regionales = Substitute.For<IClsRegionalUsuario>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsEnvioConexosNexoService _envioConexos = Substitute.For<IClsEnvioConexosNexoService>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsMargenPolitica _nacional = ConId(clsMargenPolitica.Crear("Nacional", 0.15m).Valor, Linea);
    private readonly clsUsuarioWeb _ana = UsuarioWeb("ana", "Ana Pérez");
    private readonly clsUsuarioWeb _gloria = UsuarioWeb("gloria", "Gloria");

    public clsDetalleCotizacionServiceTests()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns([_nacional]);
        _politicas.ObtenerPorIdAsync(Linea, Arg.Any<CancellationToken>()).Returns(_nacional);
        _usuarios.ObtenerPorIdAsync(_ana.Id, Arg.Any<CancellationToken>()).Returns(_ana);
        _usuarios.ObtenerPorIdAsync(_gloria.Id, Arg.Any<CancellationToken>()).Returns(_gloria);
        _envioConexos.EstadosAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid Ruta, string Conexo), string>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElDuenoVeLaComposicionDelResumenElCosteoYLaCapturaConSusTotales()
    {
        var cotizacion = Cotizada("PCN-AP-01102026-001", _ana.Id);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var detalle = (await Servicio(Comercial(_ana)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.Folio.Should().Be(cotizacion.Folio);
        detalle.SoloLectura.Should().BeFalse();
        // Las mismas proyecciones de "Mis cotizaciones" y de GET .../costeo, sin reglas nuevas.
        detalle.Cotizacion.Should().Be(clsCotizacionResumen.Desde(cotizacion, "Nacional", BaseUrl) with
        {
            Comercial = new clsComercialCotizacion(_ana.Id, "ana", "Ana Pérez"),
        });
        detalle.Costeo.Should().BeEquivalentTo(clsCosteoCotizacionService.Vista(cotizacion, BaseUrl, new Dictionary<(Guid, string), string>()));
        detalle.RowVersion.Should().Be(clsCosteoCotizacionService.VersionEtag(cotizacion)).And.Be(detalle.Costeo.RowVersion);
        detalle.Captura.Should().ContainSingle().Which.LineaSolicitudId.Should().Be(cotizacion.PrimeraRuta!.Id);
        detalle.Captura[0].LineaNegocio.Should().Be("Nacional");
        detalle.Totales.Should().Be(new clsTotalesCotizacion(1, cotizacion.PrimeraRuta.VehiculosQueCuentan.Count, null, 115m, 0m, 115m));
        // El dueño nunca carga la cotización sin filtrar por dueño ni queda en la auditoría.
        await _cotizaciones.DidNotReceiveWithAnyArgs().ObtenerPorFolioAsync(default!, Ct);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    /// <summary>
    /// Validación #179: en USD (TRM con decimales) y con varias rutas con conexos, cada precio se redondea por separado y aun así
    /// <c>precio_transporte + precio_conexos = precio_final</c> (la tarifa absorbe el redondeo), igual a la suma de las rutas del costeo; y
    /// <c>captura</c> se cruza 1 a 1 con <c>costeo.rutas[]</c> por <c>linea_solicitud_id</c>.
    /// </summary>
    [Fact]
    public async Task EnDolaresConVariasRutasYConexosLosTotalesCuadranYCapturaCruzaConCosteo()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada(
            [1_234_567m, 2_345_678m, 987_654m], folio: "PCN-AP-01102026-003", usuarioId: _ana.Id, moneda: "USD", trm: 3917.37m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_333m, 3m, orden: 1);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-2", "Escolta", 41_111m, 1m, orden: 1);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-3", "Almacenamiento", 12_345m, 7m, orden: 3);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var detalle = (await Servicio(Comercial(_ana)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.Costeo.Moneda.Should().Be("USD");
        var totales = detalle.Totales;
        totales.Rutas.Should().Be(3);
        totales.PrecioFinal.Should().NotBeNull().And.Be(detalle.Costeo.PrecioFinal);
        (totales.PrecioTransporte + totales.PrecioConexos).Should().Be(totales.PrecioFinal);
        totales.PrecioFinal.Should().Be(detalle.Costeo.Rutas.Sum(r => r.PrecioFinalLinea));
        totales.PrecioConexos.Should().Be(detalle.Costeo.ServiciosConexos.Sum(c => c.Precio)).And.BePositive();
        detalle.Captura.Select(r => r.LineaSolicitudId).Should().Equal(detalle.Costeo.Rutas.Select(r => r.LineaSolicitudId));
        detalle.Captura[0].ServiciosConexos.Select(c => c.IdServicioConexo).Should().Equal("CX-1", "CX-2");
    }

    [Fact]
    public async Task SinCosteoLosPreciosDeLosTotalesSonNullYElRestoSaleCompleto()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-01102026-002", UsuarioId = _ana.Id }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var detalle = (await Servicio(Comercial(_ana)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.Costeo.PrecioFinal.Should().BeNull();
        detalle.Totales.Should().Be(new clsTotalesCotizacion(0, 0, null, null, null, null));
        detalle.Fechas.CosteoListoAt.Should().BeNull();
        detalle.Fechas.MarcadoAt.Should().BeNull();
    }

    [Fact]
    public async Task UnComercialAjenoRecibe404SinCargarLaCotizacionDeOtro()
    {
        var luis = UsuarioWeb("luis", null);

        var resultado = await Servicio(Comercial(luis)).ObtenerAsync("PCN-AP-01102026-001", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ObtenerPorFolioAsync(default!, Ct);
        await _envioConexos.DidNotReceiveWithAnyArgs().EstadosAsync(default!, Ct);
    }

    [Fact]
    public async Task ElGerenteDeLaRegionalDelDuenoLaVeEnSoloConsultaYQuedaAuditado()
    {
        var cotizacion = Cotizada("PCN-AP-01102026-001", _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(true));

        var detalle = (await Servicio(Gerente(_gloria)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.SoloLectura.Should().BeTrue();
        detalle.Cotizacion.Comercial.Should().Be(new clsComercialCotizacion(_ana.Id, "ana", "Ana Pérez"), "el dueño, no el gerente");
        await _auditoria.Received(1).RegistrarAsync(
            Arg.Is(clsDetalleCotizacionService.HerramientaAuditoria), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Is(true), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnGerenteDeOtraRegionalRecibe404YQuedaAuditadoElRechazo()
    {
        var cotizacion = Cotizada("PCN-AP-01102026-001", _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(false));

        var resultado = await Servicio(Gerente(_gloria)).ObtenerAsync(cotizacion.Folio, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _auditoria.Received(1).RegistrarAsync(
            Arg.Is(clsDetalleCotizacionService.HerramientaAuditoria), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Is(false), Arg.Any<int>(), Arg.Any<string?>());
        await _envioConexos.DidNotReceiveWithAnyArgs().EstadosAsync(default!, Ct);
    }

    [Theory]
    [InlineData(clsCodigosAplicacion.RegionalNoAsignada)]
    [InlineData(clsCodigosAplicacion.RegionalNoDisponible)]
    public async Task SiLaRegionalDelGerenteNoSePuedeComprobarSeNiega(string codigo)
    {
        var cotizacion = Cotizada("PCN-AP-01102026-001", _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>())
            .Returns(clsResultado<bool>.Fallo(new clsErrorDominio(codigo, "sin regional")));

        var resultado = await Servicio(Gerente(_gloria)).ObtenerAsync(cotizacion.Folio, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada, "fail-closed, igual que el PDF");
    }

    [Fact]
    public async Task UnFolioInexistenteEs404TambienParaElGerente()
    {
        var resultado = await Servicio(Gerente(_gloria)).ObtenerAsync("PCN-XX-01011999-999", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public async Task SinSesionNoHayDetalle()
    {
        var resultado = await Servicio(new clsUsuarioFalso(null, null)).ObtenerAsync("PCN-AP-01102026-001", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoAutenticado);
    }

    [Fact]
    public void LasCategoriasEnMemoriaNoCalientanElCatalogoDeNexo()
    {
        var almacen = new clsAlmacenCategoriasConexos();
        var cotizacion = Cotizada("PCN-AP-01102026-001", _ana.Id);

        clsCategoriasConexosService.EnMemoria(almacen, cotizacion).Should().BeEmpty();
        almacen.UltimaLectura.Should().BeSameAs(Task.CompletedTask, "el detalle no dispara la lectura del catálogo en segundo plano");
    }

    /// <summary>Cotización con una ruta de la línea Nacional costeada: costo 100, margen 15 %, precio 115.</summary>
    private static clsCotizacionSolicitud Cotizada(string folio, Guid usuarioId)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            UsuarioId = usuarioId,
            ClienteNombre = "Cliente SA",
            NitCliente = "900111222",
            PdfPath = $"/data/pdfs/{folio}.pdf",
        }).Valor;
        cotizacion.RegistrarCotizacionChat(Linea, 100m, 0.15m, 0.15m, OrigenMargen.PoliticaLinea, "markup", 115m);
        return cotizacion;
    }

    private clsDetalleCotizacionService Servicio(IClsContextoUsuario usuario)
    {
        var opciones = Options.Create(new clsOpcionesPdf { BaseUrl = BaseUrl });
        return new(
            usuario, _cotizaciones, _usuarios, _regionales, _politicas, new clsMisCotizacionesService(_cotizaciones, _politicas, opciones),
            _envioConexos, new clsAlmacenCategoriasConexos(), _auditoria, opciones);
    }

    private static clsUsuarioWeb UsuarioWeb(string nombre, string? nombreCompleto) =>
        clsUsuarioWeb.Crear(nombre, nombreCompleto, null, "Occidente", DateTimeOffset.UtcNow).Valor;

    private static clsMargenPolitica ConId(clsMargenPolitica politica, int id)
    {
        typeof(clsMargenPolitica).GetProperty(nameof(clsMargenPolitica.Id))!.SetValue(politica, id);
        return politica;
    }

    private static clsUsuarioFalso Comercial(clsUsuarioWeb usuario) => new(usuario.Id, RolInterno.Comercial);

    private static clsUsuarioFalso Gerente(clsUsuarioWeb usuario) => new(usuario.Id, RolInterno.GerenteComercial);

    private sealed class clsUsuarioFalso(Guid? usuarioId, RolInterno? rol) : IClsContextoUsuario
    {
        public bool EstaAutenticado => UsuarioId is not null;

        public Guid? UsuarioId { get; } = usuarioId;

        public string? NombreUsuario => UsuarioId is null ? null : "usuario.prueba";

        public RolInterno? Rol { get; } = rol;
    }
}
