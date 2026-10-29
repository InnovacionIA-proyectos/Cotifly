using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Tests.Identidad;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// HU-018.1 (#155, parte C): el Excel de la cotización. El dueño recibe la versión interna (costos de Nexo en COP, márgenes y precios por
/// ruta y por camión, conexos y totales); el gerente de su regional y el director, la misma sin nada interno; los demás, 404. Respeta el
/// precio congelado y las rutas rechazadas, y solo lee lo guardado.
/// </summary>
public sealed class clsExportacionCotizacionServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsRegionalUsuario _regionales;
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsExportadorExcelCotizacion _exportador = Substitute.For<IClsExportadorExcelCotizacion>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsUsuarioWeb _ana = UsuarioWeb("ana", "Ana Pérez");
    private readonly clsUsuarioWeb _gloria = UsuarioWeb("gloria", "Gloria");
    private clsExcelCotizacion? _escrito;

    public clsExportacionCotizacionServiceTests()
    {
        _regionales = clsRegionalSustituta.Crear(_usuarios);
        _usuarios.ObtenerPorIdAsync(_ana.Id, Arg.Any<CancellationToken>()).Returns(_ana);
        _usuarios.ObtenerPorIdAsync(_gloria.Id, Arg.Any<CancellationToken>()).Returns(_gloria);
        _exportador.Generar(Arg.Do<clsExcelCotizacion>(d => _escrito = d)).Returns([1, 2, 3]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElDuenoRecibeLaVersionInternaConCostosMargenesYPreciosPorRutaYPorCamion()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], folio: "PCN-AP-27102026-001", usuarioId: _ana.Id);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 100_000m, 2m, orden: 1);
        Propia(cotizacion);

        var archivo = (await Servicio(Comercial(_ana)).ExportarAsync(cotizacion.Folio, Ct)).Valor;

        archivo.Should().BeEquivalentTo(new Cotifly.Aplicacion.Licitaciones.DTOs.clsArchivoExportado(cotizacion.Folio + ".xlsx", [1, 2, 3], clsExportacionCotizacionService.TipoXlsx));
        var datos = _escrito!;
        datos.SoloLectura.Should().BeFalse();
        datos.Comercial.Should().Be("Ana Pérez");
        datos.Rutas.Should().HaveCount(2);
        var ruta1 = datos.Rutas[0];
        ruta1.Interno!.CostoFletes.Should().Be(1_000_000m);
        ruta1.Interno.CostoConexos.Should().Be(200_000m);
        ruta1.Interno.CostoBruto.Should().Be(1_200_000m);
        ruta1.Interno.MargenAplicado.Should().Be(0.15m);
        ruta1.PrecioRuta.Should().Be(cotizacion.RutasOrdenadas[0].PrecioFinalLinea);
        (ruta1.PrecioTransporte + ruta1.PrecioConexos).Should().Be(ruta1.PrecioRuta, "la tarifa absorbe el redondeo, como en el PDF");
        ruta1.Conexos.Should().ContainSingle().Which.Interno!.Costo.Should().Be(200_000m);
        var camion = ruta1.Camiones.Should().ContainSingle().Subject;
        camion.Cuenta.Should().BeTrue();
        camion.Interno!.CostoFlete.Should().Be(1_000_000m);
        camion.Interno.Margen.Should().Be(0.15m);
        camion.PrecioTransporte.Should().Be(1_176_471m, "su costo con el margen de la ruta");
        datos.Totales.PrecioFinal.Should().Be(cotizacion.PrecioFinal);
        datos.Totales.Interno!.CostoBruto.Should().Be(3_200_000m);
        (datos.Totales.PrecioTransporte + datos.Totales.PrecioConexos).Should().Be(datos.Totales.PrecioFinal);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Theory]
    [InlineData(RolInterno.GerenteComercial)]
    [InlineData(RolInterno.Director)]
    public async Task ElGerenteYElDirectorRecibenElMismoExcelSinNadaInternoYQuedaAuditado(RolInterno rol)
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m], folio: "PCN-AP-27102026-002", usuarioId: _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(true));

        (await Servicio(new clsUsuarioFalso(_gloria.Id, rol)).ExportarAsync(cotizacion.Folio, Ct)).EsExitoso.Should().BeTrue();

        var delLector = _escrito!;
        delLector.Should().BeEquivalentTo(clsExportacionCotizacionService.Datos(cotizacion, "Ana Pérez", _reloj.AhoraUtc).SinCostos());
        delLector.SoloLectura.Should().BeTrue();
        delLector.Rutas.Should().OnlyContain(r => r.Interno == null && r.Camiones.All(c => c.Interno == null) && r.Conexos.All(c => c.Interno == null));
        delLector.Totales.Interno.Should().BeNull();
        delLector.Rutas[0].PrecioRuta.Should().Be(cotizacion.PrecioFinal, "los precios se conservan");
        await _auditoria.Received(1).RegistrarAsync(
            Arg.Is(clsExportacionCotizacionService.HerramientaAuditoria), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Is(true), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UnComercialAjenoYUnGerenteDeOtraRegionalReciben404SinExcel()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m], folio: "PCN-AP-27102026-003", usuarioId: _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(false));
        var luis = UsuarioWeb("luis", null);

        (await Servicio(Comercial(luis)).ExportarAsync(cotizacion.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        (await Servicio(new clsUsuarioFalso(_gloria.Id, RolInterno.GerenteComercial)).ExportarAsync(cotizacion.Folio, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.CotizacionNoEncontrada);

        _exportador.ReceivedCalls().Should().BeEmpty();
        await _auditoria.Received(1).RegistrarAsync(
            Arg.Is(clsExportacionCotizacionService.HerramientaAuditoria), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Is(false), Arg.Any<int>(), Arg.Any<string?>());
    }

    // Validación HU-018.1 (#155): sin regional comprobable (sin regional o fuente caída) se niega (fail-closed), queda auditado y no se
    // genera nada; sin sesión, NoAutenticado sin leer la cotización.
    [Theory]
    [InlineData(Cotifly.Aplicacion.Comun.clsCodigosAplicacion.RegionalNoAsignada)]
    [InlineData(Cotifly.Aplicacion.Comun.clsCodigosAplicacion.RegionalNoDisponible)]
    public async Task UnGerenteSinRegionalComprobableRecibe404AuditadoYSinExcel(string codigo)
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m], folio: "PCN-AP-27102026-009", usuarioId: _ana.Id);
        _cotizaciones.ObtenerPorFolioAsync(cotizacion.Folio, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _regionales.EsMiembroDeMiRegionalAsync(_gloria, _ana.Id, Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "Sin regional"));

        (await Servicio(new clsUsuarioFalso(_gloria.Id, RolInterno.GerenteComercial)).ExportarAsync(cotizacion.Folio, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.CotizacionNoEncontrada);
        (await Servicio(new clsUsuarioFalso(null, null)).ExportarAsync(cotizacion.Folio, Ct)).EsFallo.Should().BeTrue();

        _exportador.ReceivedCalls().Should().BeEmpty();
        await _auditoria.Received(1).RegistrarAsync(
            Arg.Is(clsExportacionCotizacionService.HerramientaAuditoria), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Is(false), Arg.Any<int>(), Arg.Any<string?>());
    }

    // Validación HU-018.1 (#155): el Excel de una versión -vN es el de esa versión, con su folio en el nombre del archivo.
    [Fact]
    public async Task LaVersionNuevaSeExportaConSuFolio()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m], folio: "PCN-AP-27102026-010-v2", usuarioId: _ana.Id);
        Propia(cotizacion);

        var archivo = (await Servicio(Comercial(_ana)).ExportarAsync(cotizacion.Folio, Ct)).Valor;

        archivo.Nombre.Should().Be("PCN-AP-27102026-010-v2.xlsx");
        _escrito!.Folio.Should().Be("PCN-AP-27102026-010-v2");
    }

    [Fact]
    public async Task SinPrecioEsCosteoNoListoYSinRutas404()
    {
        var sinPrecio = clsCotizacionMuestra.Formulario(folio: "PCN-AP-27102026-004", usuarioId: _ana.Id);
        var sinRutas = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-27102026-005", UsuarioId = _ana.Id }).Valor;
        Propia(sinPrecio);
        Propia(sinRutas);

        (await Servicio(Comercial(_ana)).ExportarAsync(sinPrecio.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        (await Servicio(Comercial(_ana)).ExportarAsync(sinRutas.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _exportador.ReceivedCalls().Should().BeEmpty();
    }

    /// <summary>
    /// HU-028.1 (#183): la ruta rechazada por la Mesa sale sin precio, sin conexos, sin costos y no suma; el camión rechazado de una ruta que
    /// sigue con precio no cuenta ni tiene precio ni costo (nunca un costo sin su precio, #218).
    /// </summary>
    [Fact]
    public void LasRutasYLosCamionesRechazadosNoTienenPrecioNiCostoYNoSuman()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], folio: "PCN-AP-27102026-006", usuarioId: _ana.Id);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-2", "Escolta", 50_000m, 1m, orden: 2);
        var ruta2 = cotizacion.RutasOrdenadas[1];
        cotizacion.AplicarFotoNexo([new(ruta2.VehiculosOrdenados[0].Id, EstadoCosteo.Rechazada, true, MotivoRechazo: "Sin operación")], _reloj.AhoraUtc)
            .EsExitoso.Should().BeTrue();

        var datos = clsExportacionCotizacionService.Datos(cotizacion, null, _reloj.AhoraUtc);

        var rechazada = datos.Rutas[1];
        rechazada.Rechazada.Should().BeTrue();
        rechazada.MotivoRechazo.Should().Be("Sin operación");
        (rechazada.PrecioRuta, rechazada.PrecioTransporte, rechazada.PrecioConexos).Should().Be((null, null, null));
        rechazada.Conexos.Should().BeEmpty();
        rechazada.Interno!.CostoBruto.Should().BeNull();
        rechazada.Camiones.Single().Should().Match<clsExcelCamion>(c => !c.Cuenta && c.PrecioTransporte == null && c.Interno!.CostoFlete == null);
        rechazada.Camiones.Single().Estado.Should().Be("Rechazada en NEXO");
        datos.Totales.RutasRechazadas.Should().Be(1);
        datos.Totales.PrecioFinal.Should().Be(datos.Rutas[0].PrecioRuta, "la rechazada no suma");
        datos.Totales.Interno!.CostoBruto.Should().Be(1_000_000m);
    }

    /// <summary>
    /// HU-036 (#208): con la cotización finalizada el precio queda congelado y el costo es el con que se finalizó: el Excel no recalcula con
    /// lo que Nexo trae después.
    /// </summary>
    [Fact]
    public void ConLaCotizacionFinalizadaSaleElPrecioCongeladoYElCostoConQueSeFinalizo()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_000_000m, folio: "PCN-AP-27102026-007", usuarioId: _ana.Id);
        var precio = cotizacion.PrecioFinal;
        cotizacion.Finalizar(_ana.Id, _reloj.AhoraUtc).EsExitoso.Should().BeTrue();
        // Nexo cambia el flete después de finalizar: el polling solo actualiza el estado (el precio y el costo quedan congelados).
        var precioCosteo = new Cotifly.Aplicacion.Costeo.Services.clsPrecioCosteoService();
        precioCosteo.ActualizarCamiones(cotizacion, clsCotizacionMuestra.Foto([3_000_000m]), _reloj.AhoraUtc);
        precioCosteo.AplicarPrecio(cotizacion, clsCotizacionMuestra.Foto([3_000_000m]), _reloj.AhoraUtc);

        var datos = clsExportacionCotizacionService.Datos(cotizacion, null, _reloj.AhoraUtc);

        datos.PrecioCongelado.Should().BeTrue();
        datos.FinalizadaAt.Should().Be(_reloj.AhoraUtc);
        datos.Totales.PrecioFinal.Should().Be(precio);
        datos.Rutas[0].Interno!.CostoBruto.Should().Be(2_000_000m);
        datos.Rutas[0].Camiones[0].PrecioTransporte.Should().Be(precio);
    }

    [Fact]
    public void EnDolaresLosPreciosVanEnDolaresYLosCostosSiempreEnPesos()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 3_917_370m, folio: "PCN-AP-27102026-008", usuarioId: _ana.Id, moneda: "USD", trm: 3917.37m);

        var datos = clsExportacionCotizacionService.Datos(cotizacion, null, _reloj.AhoraUtc);

        datos.Moneda.Should().Be("USD");
        datos.TrmAplicada.Should().Be(3917.37m);
        datos.Rutas[0].Interno!.CostoBruto.Should().Be(3_917_370m, "el costeo de Nexo es en pesos y nunca se convierte");
        datos.Rutas[0].PrecioRuta.Should().Be(cotizacion.PrecioFinal).And.BeLessThan(2_000m);
        datos.Rutas[0].Camiones[0].PrecioTransporte.Should().Be(datos.Rutas[0].PrecioRuta);
    }

    private void Propia(clsCotizacionSolicitud cotizacion) =>
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);

    private clsExportacionCotizacionService Servicio(IClsContextoUsuario usuario) =>
        new(usuario, _cotizaciones, _usuarios, _regionales, _auditoria, _exportador, _reloj);

    private static clsUsuarioWeb UsuarioWeb(string nombre, string? nombreCompleto) =>
        clsUsuarioWeb.Crear(nombre, nombreCompleto, null, "Occidente", DateTimeOffset.UtcNow).Valor;

    private static clsUsuarioFalso Comercial(clsUsuarioWeb usuario) => new(usuario.Id, RolInterno.Comercial);

    private sealed class clsUsuarioFalso(Guid? usuarioId, RolInterno? rol) : IClsContextoUsuario
    {
        public bool EstaAutenticado => UsuarioId is not null;

        public Guid? UsuarioId { get; } = usuarioId;

        public string? NombreUsuario => UsuarioId is null ? null : "usuario.prueba";

        public RolInterno? Rol { get; } = rol;
    }
}
