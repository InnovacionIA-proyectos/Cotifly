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
        var vista = clsCosteoCotizacionService.Vista(EnDolares(precioFinal: 750m), null);

        vista.Moneda.Should().Be("USD");
        vista.TrmAplicada.Should().Be(4000m);
        vista.PrecioFinal.Should().Be(750m);
        vista.ServiciosConexos.Single().Precio.Should().Be(25m);
        clsCosteoCotizacionService.Vista(clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-C"), null).TrmAplicada.Should().Be(0m);
    }

    [Fact]
    public async Task ElPdfEnDolaresLlevaLasTarifasEnUsdYLaNotaDeTrmMinimaConLaTrmFijada()
    {
        var cotizacion = Registrada(EnDolares(precioFinal: 750m));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.NotaMoneda.Should().Be(clsTextosEstandar.NotaTrmMinima(4000m));
        _documento.NotaMoneda.Should().Contain("TRM mínima de $4.000,00");
    }

    [Fact]
    public async Task UnPrecioEnDolaresQueNoCorrespondeAlCostoYLaTrmNoSeImprime()
    {
        var cotizacion = Registrada(EnDolares(precioFinal: 760m));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido, "2.550.000 / 0,85 / 4.000 = 750,00, no 760");
        await _generador.DidNotReceiveWithAnyArgs().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    // #92, #95: con el turno de render agotado no se guarda el PDF ni cambia la versión.
    [Fact]
    public async Task SiElTurnoDeRenderSeAgotaElPdfDelCosteoEsPdfRenderNoDisponible()
    {
        var cotizacion = Registrada(EnDolares(precioFinal: 750m));
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

    /// <summary>Costeada en USD con la TRM 4.000 fijada: flete 2.550.000 COP al 15 % (750,00 USD) y el conexo Cargue de 85.000 (25,00 USD) incluido.</summary>
    private clsCotizacion EnDolares(decimal precioFinal)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-05102026-010",
            UsuarioId = _ana,
            Moneda = "USD",
            TrmAplicada = 4000m,
            Costo = 2_550_000m,
            PrecioFinal = precioFinal,
            MargenPolitica = 0.15m,
            MargenAplicado = 0.15m,
            OrigenMargen = OrigenMargen.PoliticaLinea,
            MetodoCalculo = "margen",
            EstadoCosteo = EstadoCosteo.Autocosteo,
        }).Valor;
        cotizacion.SeleccionarServiciosConexos(clsServiciosConexosJson.Escribir([new clsServicioConexoCosteo("CX-1", "Cargue", "logistico", 1m, 85_000m, 25m, true)]));
        typeof(clsCotizacion).GetProperty(nameof(clsCotizacion.VersionNegocio))!.SetValue(cotizacion, Version);
        return Preparada(cotizacion);
    }

    private static clsCotizacion Preparada(clsCotizacion cotizacion)
    {
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarEnvioCosteo("SOL-1", true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        cotizacion.ActualizarClienteDelCatalogo("CLIENTE S.A.S.", null);
        return cotizacion;
    }

    private clsCotizacion Registrada(clsCotizacion cotizacion)
    {
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _cotizaciones.ListarLineasAsync(cotizacion.Id, Arg.Any<CancellationToken>()).Returns([]);
        typeof(clsCotizacion).GetProperty(nameof(clsCotizacion.LineaNegocioId))!.SetValue(cotizacion, 3);
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Importacion", 0.15m).Valor);
        return cotizacion;
    }

    private clsCosteoCotizacionService Servicio() => new(
        _cotizaciones, _politicas, _usuarios, _generador, _almacen, _unidad,
        Substitute.For<IClsCalendarioNegocio>(), Substitute.For<IClsAuditoriaService>(), Options.Create(new clsOpcionesPdf()),
        NullLogger<clsCosteoCotizacionService>.Instance);
}
