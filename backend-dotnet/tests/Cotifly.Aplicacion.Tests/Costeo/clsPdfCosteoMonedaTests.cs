using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Costeo en dólares del formulario (ADR-014 §5): la vista expone la TRM fijada, y el PDF lleva el flete y los conexos en
/// USD (convertidos con esa TRM), pasa la validación de coherencia contra el costo en COP y trae la nota de TRM mínima. En COP, sin nota.
/// </summary>
public sealed class clsPdfCosteoMonedaTests
{
    private const int Version = 7;

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsGeneradorPdf _generador = Substitute.For<IClsGeneradorPdf>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly Guid _ana = Guid.NewGuid();
    private clsDocumentoCotizacion? _documento;

    public clsPdfCosteoMonedaTests()
    {
        _usuarios.ObtenerPorIdAsync(_ana, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("ana", "Ana Pérez", "ana@boterosoto.com.co", "Occidente", DateTimeOffset.UnixEpoch).Valor);
        _generador.GenerarAsync(Arg.Do<clsDocumentoCotizacion>(d => _documento = d), Arg.Any<CancellationToken>()).Returns(new clsPdfGenerado([1, 2, 3], 1));
        _almacen.GuardarNuevoAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(ci => "/data/pdfs/" + ci.Arg<string>());
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Version, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void LaVistaDelCosteoExponeLaMonedaYLaTrmFijada()
    {
        var vista = clsCosteoCotizacionService.Vista(EnDolares(), null);

        vista.Moneda.Should().Be("USD");
        vista.TrmAplicada.Should().Be(4000m);
        vista.PrecioFinal.Should().Be(771.25m, "HU-021: el precio de la ruta incluye el conexo (750,00 del flete + 21,25 del Cargue, HU-043.2: sin margen)");
        vista.ServiciosConexos.Single().Precio.Should().Be(21.25m, "85.000 / 4.000, sin margen");
        clsCosteoCotizacionService.Vista(clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-C"), null).TrmAplicada.Should().Be(0m);
    }

    [Fact]
    public async Task ElPdfEnDolaresLlevaLasTarifasEnUsdYLaNotaDeTrmMinimaConLaTrmFijada()
    {
        var cotizacion = Registrada(EnDolares());

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.Secciones[0].Filas[0].Tarifa.Should().Be("US$771,25", "HU-033 (#199): la tarifa es el precio final de la ruta, con el conexo incluido");
        _documento.Incluye.Should().Contain(i => i.StartsWith("Cargue", StringComparison.Ordinal) && !i.Contains("US$", StringComparison.Ordinal),
            "el conexo se lista como incluido, sin precio");
        _documento.TotalGeneral!.Valor.Should().Be("US$771,25 USD");
        _documento.NotaMoneda.Should().Be(clsTextosEstandar.NotaTrmMinima(4000m));
        _documento.NotaMoneda.Should().Contain("TRM mínima de $4.000,00");
    }

    // El precio no se guarda aparte del costo (la ruta lo calcula con su margen), así que la cotización no puede traer un
    // precio incoherente; la guarda de coherencia del PDF se prueba con el payload alterado y la trazabilidad que arma el servicio.
    [Fact]
    public void UnPrecioEnDolaresQueNoEsElPrecioFinalDeLaRutaNoSeImprime()
    {
        // La tarifa impresa es el precio final de la ruta (precioFinalLinea, con el conexo): 750,00 + 21,25 = 771,25 (conexo sin margen).
        var cotizacion = EnDolares();
        var ruta = cotizacion.PrimeraRuta!;
        var payload = clsCosteoCotizacionService.Payload(cotizacion, "Importacion", "USD");
        payload.Tarifa!.Filas![0].Tarifa = 760m;
        var traza = new clsTrazabilidadCotizacion(
            null, ruta.MargenPoliticaSugerido, null, null, ruta.MetodoCalculo, ruta.PrecioFinalLinea, cotizacion.NitCliente, null, "USD",
            cotizacion.TrmAplicada);

        var error = clsGenerarPdfService.ValidarCoherencia(payload, traza, "margen", "USD");

        error!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido, "el precio final de la ruta es 771,25, no 760");
        ruta.PrecioFinalLinea.Should().Be(771.25m);
        clsGenerarPdfService.ValidarCoherencia(clsCosteoCotizacionService.Payload(cotizacion, "Importacion", "USD"), traza, "margen", "USD")
            .Should().BeNull("el payload que arma el servicio imprime el precio final de la ruta");
    }

    // Con el turno de render agotado no se guarda el PDF ni cambia la versión.
    [Fact]
    public async Task SiElTurnoDeRenderSeAgotaElPdfDelCosteoEsPdfRenderNoDisponible()
    {
        var cotizacion = Registrada(EnDolares());
        _generador.GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new clsRenderPdfOcupadoException(TimeSpan.FromSeconds(30)));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfRenderNoDisponible);
        await _almacen.DidNotReceiveWithAnyArgs().GuardarNuevoAsync(default!, default!, Ct);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ElPdfEnPesosNoLlevaLaNotaDeTrm()
    {
        var cotizacion = Registrada(Preparada(clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-011")));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.NotaMoneda.Should().BeNull();
    }

    // El texto de seguros de la configuración (Pdf:TextoSeguros) llega al documento partido en párrafos, incluso en una
    // cotización ya finalizada (Preparada la finaliza): el PDF se regenera con el texto vigente, no reescribe los ya generados.
    [Fact]
    public async Task ElPdfDelCosteoLlevaElTextoDeSegurosDeLaConfiguracionPartidoEnParrafos()
    {
        var cotizacion = Registrada(Preparada(clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-013")));

        var resultado = await ServicioConSeguros("Primer párrafo del seguro.\n\nSegundo párrafo del seguro.")
            .GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.TextoSeguros.Should().Equal("Primer párrafo del seguro.", "Segundo párrafo del seguro.");
    }

    [Fact]
    public async Task SinTextoDeSegurosEnLaConfiguracionElPdfNoLoTrae()
    {
        var cotizacion = Registrada(Preparada(clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-014")));

        var resultado = await ServicioConSeguros("   ").GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.TextoSeguros.Should().BeNull();
    }

    /// <summary>
    /// Por el camino real de <c>GenerarPdfAsync</c> (payload, validador, constructor): un pliego con rutas
    /// de dos líneas de negocio sale con la cabecera "Varias" y cada sección con la línea de su ruta, sin PDF_PAYLOAD_INVALIDO.
    /// </summary>
    [Fact]
    public async Task UnPliegoConRutasDeLineasDistintasSaleConCadaSeccionEnSuLinea()
    {
        var pliego = Preparada(clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], folio: "PCN-900555111-AP-05102026-012", usuarioId: _ana));
        typeof(clsLineaSolicitud).GetProperty(nameof(clsLineaSolicitud.LineaNegocioId))!.SetValue(pliego.RutasOrdenadas[1], 7);
        clsCotizacionMuestra.ConVersionNegocio(pliego, Version);
        Registrada(pliego);
        _politicas.ObtenerPorIdAsync(7, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Exportacion", 0.15m).Valor);

        var resultado = await Servicio().GenerarPdfAsync(_ana, pliego.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.Variante.Should().Be(VarianteDocumento.Pliego);
        _documento.Secciones.Select(s => s.Titulo).Should().Equal("RUTA 1: PLIEGO DE TRANSPORTE IMPORTACION", "RUTA 2: PLIEGO DE TRANSPORTE EXPORTACION");
        clsCosteoCotizacionService.PayloadPliego(pliego, null, "COP", new Dictionary<int, string> { [3] = "Importacion", [7] = "Exportacion" })
            .LineaNegocio.Should().Be("Varias");
    }

    /// <summary>En USD la tarifa absorbe a lo sumo 0,01 por conexo, no un dólar entero.</summary>
    [Fact]
    public void EnDolaresLaTarifaSoloAbsorbeElRedondeoDeUnCentavoPorConexo()
    {
        var cotizacion = EnDolares();
        var ruta = cotizacion.PrimeraRuta!;
        clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta).Should().Be(750m);

        ruta.FijarPrecio(ruta.PrecioFinalLinea!.Value + 0.5m); // una ruta que no cuadra por medio dólar (p. ej. migrada)
        clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta).Should().Be(750m, "0,50 USD no es redondeo: la tarifa es el transporte calculado");

        ruta.FijarPrecio(ruta.PrecioFinalLinea!.Value - 0.49m);
        clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta).Should().Be(750.01m, "un centavo sí es redondeo y la tarifa lo absorbe");
    }

    /// <summary>
    /// Costeada en USD con la TRM 4.000 fijada: flete 2.550.000 COP al 15 % (750,00 USD) y el conexo Cargue de 85.000 (21,25 USD, sin margen) en la
    /// ruta (el precio de la ruta, 771,25 USD, incluye el conexo a su venta).
    /// </summary>
    private clsCotizacionSolicitud EnDolares()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(
            flete: 2_550_000m, margen: 0.15m, folio: "PCN-900555111-AP-05102026-010", usuarioId: _ana, moneda: "USD", trm: 4000m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        return Preparada(clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version));
    }

    /// <summary>La costeada ya viene del formulario y enviada; solo falta el cliente del catálogo.</summary>
    private static clsCotizacionSolicitud Preparada(clsCotizacionSolicitud cotizacion)
    {
        cotizacion.ActualizarClienteDelCatalogo("CLIENTE S.A.S.", null);
        // El PDF se genera con la cotización finalizada.
        cotizacion.Finalizar(cotizacion.UsuarioId ?? Guid.NewGuid(), clsCotizacionMuestra.Ahora);
        return cotizacion;
    }

    private clsCotizacionSolicitud Registrada(clsCotizacionSolicitud cotizacion)
    {
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Importacion", 0.15m).Valor);
        return cotizacion;
    }

    private clsCosteoCotizacionService Servicio() => new(
        _cotizaciones, _politicas, _usuarios, _generador, _almacen, _unidad,
        Substitute.For<IClsCalendarioNegocio>(), Substitute.For<IClsAuditoriaService>(), Options.Create(new clsOpcionesPdf()),
        NullLogger<clsCosteoCotizacionService>.Instance);

    /// <summary>El servicio con un texto de seguros concreto en la configuración.</summary>
    private clsCosteoCotizacionService ServicioConSeguros(string? textoSeguros) => new(
        _cotizaciones, _politicas, _usuarios, _generador, _almacen, _unidad,
        Substitute.For<IClsCalendarioNegocio>(), Substitute.For<IClsAuditoriaService>(),
        Options.Create(new clsOpcionesPdf { TextoSeguros = textoSeguros }),
        NullLogger<clsCosteoCotizacionService>.Instance);
}
