using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
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
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// PDF del costeo y concurrencia (ADR-018 §8 y §12): el PDF se confirma SIEMPRE contra la versión de negocio que se cargó, aunque el
/// cliente no envíe <c>If-Match</c>; si otro flujo cambió la cotización mientras se generaba, responde <c>CONFLICTO_CONCURRENCIA</c> y
/// no deja vigente un PDF con el precio viejo.
/// </summary>
public sealed class clsPdfCosteoConcurrenciaTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsGeneradorPdf _generador = Substitute.For<IClsGeneradorPdf>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsPdfCosteoConcurrenciaTests()
    {
        // HU-021: la costeada ya viene del formulario (ruta 1 con la línea de negocio 3) y enviada a Nexo.
        // HU-036.3 (#207): el PDF se genera con la cotización finalizada.
        _cotizacion = clsAjusteMargenCosteoServiceTests.Finalizada(_ana, "PCN-900555111-AP-05102026-009");
        _cotizacion.ActualizarClienteDelCatalogo("CLIENTE S.A.S.", null);
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(Cotifly.Dominio.Margen.clsMargenPolitica.Crear("Importacion", 0.15m).Valor);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _usuarios.ObtenerPorIdAsync(_ana, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("ana", "Ana Pérez", "ana@boterosoto.com.co", "Occidente", DateTimeOffset.UnixEpoch).Valor);
        _generador.GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>()).Returns(new clsPdfGenerado([1, 2, 3], 1));
        _almacen.GuardarNuevoAsync(_cotizacion.Folio + ".pdf", Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns("/data/pdfs/" + _cotizacion.Folio + ".pdf");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HU0363_SinFinalizarElPdfEsCotizacionNoFinalizadaYNoSeRenderiza()
    {
        var sinFinalizar = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-078");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(sinFinalizar.Folio, _ana, Arg.Any<CancellationToken>()).Returns(sinFinalizar);

        var resultado = await Servicio().GenerarPdfAsync(_ana, sinFinalizar.Folio, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        resultado.Error.Mensaje.Should().StartWith($"Finaliza la cotización {sinFinalizar.Folio} antes de generar el PDF");
        resultado.Error.Datos!["accion"].Should().Be("finalizar");
        await _generador.DidNotReceiveWithAnyArgs().GenerarAsync(default!, Ct);
        sinFinalizar.PdfPath.Should().BeNull();
    }

    [Fact]
    public async Task HU0363_FinalizadaYYaEnviadaElPdfSigueSiendoPdfYaEnviado()
    {
        _cotizacion.RegistrarEnvio();

        var resultado = await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
    }

    // OBS-1027-6 (#226, E2E real del 2026-10-27): con el PDF vigente y la cotización ya entregada al cliente (enviada y, en el E2E, con
    // resultado), el PDF responde PDF_YA_ENVIADO (crear una versión nueva), no PDF_YA_GENERADO: la entrega manda sobre el PDF vigente.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConElPdfVigenteYYaEnviadaElPdfEsPdfYaEnviadoNoPdfYaGenerado(bool conResultado)
    {
        _cotizacion.AsignarPdf("/data/pdfs/" + _cotizacion.Folio + ".pdf");
        _cotizacion.RegistrarEnvio();
        if (conResultado)
        {
            _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, clsCotizacionMuestra.Ahora);
        }

        var resultado = await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        await _generador.DidNotReceiveWithAnyArgs().GenerarAsync(default!, Ct);
    }

    [Fact]
    public async Task ConElPdfVigenteSinEnviarSigueSiendoPdfYaGenerado()
    {
        _cotizacion.AsignarPdf("/data/pdfs/" + _cotizacion.Folio + ".pdf");

        var resultado = await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaGenerado);
    }

    [Fact]
    public async Task SinIfMatchElPdfSeConfirmaContraLaVersionCargadaYSubeLaVersion()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, 7, Arg.Any<CancellationToken>()).Returns(true);

        var resultado = await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        resultado.Valor.RowVersion.Should().Be("8");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiLaCotizacionCambioMientrasSeGenerabaRespondeConflictoYBorraElPdfNuevo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, 7, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _almacen.Received(1).EliminarAsync(_cotizacion.Folio + ".pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConUnIfMatchVencidoNiSiquieraGenera()
    {
        var resultado = await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, 6, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _generador.DidNotReceiveWithAnyArgs().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    private clsCosteoCotizacionService Servicio() => new(
        _cotizaciones, _politicas, _usuarios, _generador, _almacen, _unidad,
        Substitute.For<IClsCalendarioNegocio>(), Substitute.For<IClsAuditoriaService>(), Options.Create(new clsOpcionesPdf()),
        NullLogger<clsCosteoCotizacionService>.Instance);
}
