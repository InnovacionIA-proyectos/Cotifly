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
using Cotifly.Aplicacion.Tests.Identidad;
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
    private readonly IClsRegionalUsuario _regionales;
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsEnvioConexosNexoService _envioConexos = Substitute.For<IClsEnvioConexosNexoService>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsMargenPolitica _nacional = ConId(clsMargenPolitica.Crear("Nacional", 0.15m).Valor, Linea);
    private readonly clsUsuarioWeb _ana = UsuarioWeb("ana", "Ana Pérez");
    private readonly clsUsuarioWeb _gloria = UsuarioWeb("gloria", "Gloria");

    public clsDetalleCotizacionServiceTests()
    {
        _regionales = clsRegionalSustituta.Crear(_usuarios);
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

    /// <summary>
    /// HU-043.2 (#251): <c>precio_transporte</c> es (flete + retorno + levante) con el margen y <c>precio_conexos</c> la suma de las VENTAS
    /// de los conexos, sin margen; suman el precio final. El costo de los conexos no sale en ninguna parte del detalle para el comercial.
    /// </summary>
    [Fact]
    public async Task Hu0432_LosTotalesSeparanElTransporteConMargenYLaVentaDeLosConexosSinMargen()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: "PCN-AP-01102026-007", usuarioId: _ana.Id);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, 2m, ventaUnitaria: 100_000m);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var detalle = (await Servicio(Comercial(_ana)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.Totales.PrecioTransporte.Should().Be(3_000_000m, "2.550.000 / (1 − 0,15)");
        detalle.Totales.PrecioConexos.Should().Be(200_000m, "100.000 × 2, sin margen");
        detalle.Totales.PrecioFinal.Should().Be(3_200_000m);
        detalle.Costeo.ServiciosConexos.Should().ContainSingle().Which.Precio.Should().Be(200_000m, "el detalle muestra la venta, no el costo (170.000)");
        detalle.Costeo.Rutas.Single().CostoFinal.Should().Be(2_720_000m, "costo_final (HU-039.1) sigue con el costo del conexo");
    }

    /// <summary>
    /// HU-043.2 (#251): una cotización FINALIZADA antes del cambio conserva sus valores: el precio congelado (con el margen sobre los
    /// conexos) no se recalcula y el desglose se sigue mostrando como entonces, así que transporte + conexos sigue dando su precio.
    /// </summary>
    [Fact]
    public async Task Hu0432_UnaCotizacionFinalizadaAntesDelCambioConservaSuPrecioYSuDesglose()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: "PCN-AP-01102026-008", usuarioId: _ana.Id);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        // Así quedó guardada con la fórmula anterior: (2.550.000 + 85.000) / 0,85 = 3.100.000.
        cotizacion.PrimeraRuta!.FijarPrecio(3_100_000m);
        cotizacion.RecalcularPrecio(clsCotizacionMuestra.Ahora);
        cotizacion.Finalizar(_ana.Id, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var detalle = (await Servicio(Comercial(_ana)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.Totales.PrecioFinal.Should().Be(3_100_000m, "el precio finalizado no cambia");
        detalle.Totales.PrecioConexos.Should().Be(100_000m, "85.000 / 0,85: el conexo como se cotizó");
        detalle.Totales.PrecioTransporte.Should().Be(3_000_000m);
        (detalle.Totales.PrecioTransporte + detalle.Totales.PrecioConexos).Should().Be(detalle.Totales.PrecioFinal);
    }

    /// <summary>Fusión #181: el detalle trae por camión el estado y el motivo pendiente de la línea de Nexo, igual que <c>GET .../costeo</c>.</summary>
    [Fact]
    public async Task ElDetalleTraeElEstadoYElMotivoPendienteDeLaLineaDeNexoComoElCosteo()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: null, folio: "PCN-AP-01102026-004", usuarioId: _ana.Id);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        var camion = cotizacion.Vehiculos[0];
        var situaciones = Substitute.For<Cotifly.Aplicacion.Costeo.Interfaces.IClsSituacionLineasNexo>();
        var porCamion = new Dictionary<Guid, clsSituacionLineaNexo>
        {
            [camion.Id] = new("por_costear", "Línea sin vehículo: requiere consulta completa") { LineaNexoId = camion.LineaSolicitudNexoId },
        };
        situaciones.Obtener(cotizacion.Id).Returns(porCamion);
        var opciones = Options.Create(new clsOpcionesPdf { BaseUrl = BaseUrl });
        var servicio = new clsDetalleCotizacionService(
            Comercial(_ana), _cotizaciones, _usuarios, _regionales, _politicas, new clsMisCotizacionesService(_cotizaciones, _politicas, opciones),
            _envioConexos, new clsAlmacenCategoriasConexos(), _auditoria, opciones, situaciones);

        var detalle = (await servicio.ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        var deDetalle = detalle.Costeo.Rutas.Single().Camiones.Single();
        (deDetalle.EstadoLineaNexo, deDetalle.MotivoPendiente).Should().Be(("por_costear", "Línea sin vehículo: requiere consulta completa"));
        detalle.Costeo.Should().BeEquivalentTo(
            clsCosteoCotizacionService.Vista(cotizacion, BaseUrl, new Dictionary<(Guid, string), string>(), null, porCamion));
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

    /// <summary>
    /// HU-039.1 (#218): el costo bruto de la ruta (<c>costeo.rutas[].costo_final</c>) es solo del dueño. El gerente de su regional ve el
    /// mismo detalle en solo consulta, pero con <c>costo_final</c> en <c>null</c> (decisión conservadora: hasta HU-039 el detalle nunca le
    /// mostraba costos).
    /// </summary>
    [Fact]
    public async Task ElDuenoVeElCostoBrutoDeLaRutaYElGerenteEnSoloConsultaNo()
    {
        var cotizacion = Cotizada("PCN-AP-01102026-005", _ana.Id);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(true));

        var delDueno = (await Servicio(Comercial(_ana)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;
        var delGerente = (await Servicio(Gerente(_gloria)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        delDueno.SoloLectura.Should().BeFalse();
        delDueno.Costeo.Rutas.Single().CostoFinal.Should().Be(100m);
        delGerente.SoloLectura.Should().BeTrue();
        delGerente.Costeo.Rutas.Single().CostoFinal.Should().BeNull("el gerente en solo consulta no ve el costo bruto");
        delGerente.Costeo.Should().BeEquivalentTo(delDueno.Costeo.SinCostoFinal(), "solo cambia el costo");
        delGerente.Totales.Should().Be(delDueno.Totales);
    }

    [Fact]
    public async Task ElDirectorVeLaDeCualquierRegionalEnSoloConsultaYQuedaAuditadoConSuRol()
    {
        // HU-037.3 (#211): sin consultar regionales (el director no tiene alcance regional).
        var dora = UsuarioWeb("dora", "Dora");
        _usuarios.ObtenerPorIdAsync(dora.Id, Arg.Any<CancellationToken>()).Returns(dora);
        var cotizacion = Cotizada("PCN-AP-01102026-001", _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var detalle = (await Servicio(new clsUsuarioFalso(dora.Id, RolInterno.Director)).ObtenerAsync(cotizacion.Folio, Ct)).Valor;

        detalle.SoloLectura.Should().BeTrue();
        await _regionales.DidNotReceiveWithAnyArgs().EsMiembroDeMiRegionalAsync(default!, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(
            Arg.Is(clsDetalleCotizacionService.HerramientaAuditoria),
            Arg.Is<object?>(e => e!.ToString()!.Contains("rol = director", StringComparison.Ordinal)),
            Arg.Any<object?>(), Arg.Is(true), Arg.Any<int>(), Arg.Any<string?>());
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
