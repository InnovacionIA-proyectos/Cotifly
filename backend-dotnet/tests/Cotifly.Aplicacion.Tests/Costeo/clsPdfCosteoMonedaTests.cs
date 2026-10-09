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
/// Costeo en dólares del formulario (HU-013.7, ADR-014 §5): la vista expone la TRM fijada, y el PDF lleva el flete y los conexos en
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
        vista.PrecioFinal.Should().Be(775m, "HU-021: el precio de la ruta incluye el conexo (750,00 del flete + 25,00 del Cargue)");
        vista.ServiciosConexos.Single().Precio.Should().Be(25m);
        clsCosteoCotizacionService.Vista(clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-C"), null).TrmAplicada.Should().Be(0m);
    }

    [Fact]
    public async Task ElPdfEnDolaresLlevaLasTarifasEnUsdYLaNotaDeTrmMinimaConLaTrmFijada()
    {
        var cotizacion = Registrada(EnDolares());

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.Secciones[0].Filas[0].Tarifa.Should().Be("US$750,00", "la tarifa es el precio del transporte; el conexo va aparte");
        _documento.NotaMoneda.Should().Be(clsTextosEstandar.NotaTrmMinima(4000m));
        _documento.NotaMoneda.Should().Contain("TRM mínima de $4.000,00");
    }

    // HU-021: el precio ya no se guarda aparte del costo (la ruta lo calcula con su margen), así que la cotización no puede traer un
    // precio incoherente; la guarda de coherencia del PDF se prueba con el payload alterado y la trazabilidad que arma el servicio.
    [Fact]
    public void UnPrecioEnDolaresQueNoCorrespondeAlCostoYLaTrmNoSeImprime()
    {
        var cotizacion = EnDolares();
        var ruta = cotizacion.PrimeraRuta!;
        var payload = clsCosteoCotizacionService.Payload(cotizacion, "Importacion", "USD");
        payload.Tarifa!.Filas![0].Tarifa = 760m;
        var traza = new clsTrazabilidadCotizacion(
            ruta.CostoTransporte, ruta.MargenPoliticaSugerido, ruta.MargenAplicado, null, ruta.MetodoCalculo, 760m, cotizacion.NitCliente, null, "USD",
            cotizacion.TrmAplicada);

        var error = clsGenerarPdfService.ValidarCoherencia(payload, traza, "margen", "USD");

        error!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido, "2.550.000 / 0,85 / 4.000 = 750,00, no 760");
        clsGenerarPdfService.ValidarCoherencia(clsCosteoCotizacionService.Payload(cotizacion, "Importacion", "USD"), traza with { PrecioFinal = 750m }, "margen", "USD")
            .Should().BeNull("el payload que arma el servicio es coherente con el costo y la TRM");
    }

    // #92, #95: con el turno de render agotado no se guarda el PDF ni cambia la versión.
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

    /// <summary>
    /// Costeada en USD con la TRM 4.000 fijada: flete 2.550.000 COP al 15 % (750,00 USD) y el conexo Cargue de 85.000 (25,00 USD) en la
    /// ruta (HU-021: el precio de la ruta, 775,00 USD, incluye el conexo).
    /// </summary>
    private clsCotizacionSolicitud EnDolares()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(
            flete: 2_550_000m, margen: 0.15m, folio: "PCN-900555111-AP-05102026-010", usuarioId: _ana, moneda: "USD", trm: 4000m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        return Preparada(clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version));
    }

    /// <summary>HU-021: la costeada ya viene del formulario y enviada; solo falta el cliente del catálogo.</summary>
    private static clsCotizacionSolicitud Preparada(clsCotizacionSolicitud cotizacion)
    {
        cotizacion.ActualizarClienteDelCatalogo("CLIENTE S.A.S.", null);
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
}
