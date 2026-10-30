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

    /// <summary>
    /// DEF-1027B-1 (#228, E2E real del 2026-10-27B): el 409 <c>PDF_YA_ENVIADO</c> del PDF llegaba sin <c>detalle</c>. Trae la llamada para
    /// crear la versión nueva, igual que margen, conexos, textos y re-solicitar (contrato §4.5).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DEF1027B1_ElPdfYaEnviadoTraeLaLlamadaParaLaVersionNueva(bool conPdf, bool conResultado)
    {
        if (conPdf)
        {
            _cotizacion.AsignarPdf("/data/pdfs/" + _cotizacion.Folio + ".pdf");
        }

        _cotizacion.RegistrarEnvio();
        if (conResultado)
        {
            _cotizacion.RegistrarResultadoPendienteNexo(true, null, null, clsCotizacionMuestra.Ahora);
        }

        var error = (await Servicio().GenerarPdfAsync(_ana, _cotizacion.Folio, null, Ct)).Error;

        error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        error.Datos.Should().NotBeNull();
        error.Datos!["accion"].Should().Be("nueva_version");
        error.Datos["metodo"].Should().Be("POST");
        error.Datos["endpoint"].Should().Be($"/web/cotizaciones/{_cotizacion.Folio}/costeo/re-solicitar");
        var cuerpo = error.Datos["cuerpo"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        cuerpo["motivo"].Should().Be(clsCosteoCotizacionService.MotivoVersionPdf);
        cuerpo["nueva_version"].Should().Be(true);
    }

    /// <summary>
    /// Validación de OBS-1027-6 (#226): la precedencia nueva no cambia los demás casos. Una enviada ANTES de HU-036 (sin finalizar, sin
    /// backfill) responde <c>PDF_YA_ENVIADO</c> con o sin PDF vigente (nunca <c>COTIZACION_NO_FINALIZADA</c>), y una sin finalizar ni
    /// enviada con PDF vigente sigue siendo <c>PDF_YA_GENERADO</c> (el PDF vigente sigue antes de la finalización).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaEnviadaAntesDeHu036EsPdfYaEnviadoConOSinPdf(bool conPdf)
    {
        var anterior = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-079");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(anterior.Folio, _ana, Arg.Any<CancellationToken>()).Returns(anterior);
        if (conPdf)
        {
            anterior.AsignarPdf("/data/pdfs/" + anterior.Folio + ".pdf");
        }

        anterior.RegistrarEnvio();

        var resultado = await Servicio().GenerarPdfAsync(_ana, anterior.Folio, null, Ct);

        anterior.Finalizada.Should().BeFalse();
        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        await _generador.DidNotReceiveWithAnyArgs().GenerarAsync(default!, Ct);
    }

    [Fact]
    public async Task SinFinalizarNiEnviarConElPdfVigenteSigueSiendoPdfYaGenerado()
    {
        var sinFinalizar = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-080");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(sinFinalizar.Folio, _ana, Arg.Any<CancellationToken>()).Returns(sinFinalizar);
        sinFinalizar.AsignarPdf("/data/pdfs/" + sinFinalizar.Folio + ".pdf");

        var resultado = await Servicio().GenerarPdfAsync(_ana, sinFinalizar.Folio, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaGenerado);
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
