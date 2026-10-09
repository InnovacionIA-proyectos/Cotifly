using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Pdf;

/// <summary>
/// Caso de uso <c>generar_pdf</c> con puertos simulados: orden de validaciones, firma desde <c>UsuariosWeb</c> (celular solo
/// si hay teléfono), línea resuelta antes de renderizar, folio que no se repite ni reemplaza un PDF, compensación y salida
/// <c>SalidaPdf</c>. Port de <c>test_generar_pdf_*</c> de <c>tests/test_pdf.py</c> (sin weasyprint ni base).
/// </summary>
public sealed class clsGenerarPdfServiceTests
{
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenResolverService _margen = Substitute.For<IClsMargenResolverService>();
    private readonly IClsGeneradorPdf _generador = Substitute.For<IClsGeneradorPdf>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly IClsCalendarioNegocio _calendario = Substitute.For<IClsCalendarioNegocio>();
    private readonly clsOpcionesPdf _opciones = new();
    private readonly clsUsuarioWeb _usuario;
    private readonly clsMargenPolitica _nacional = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;
    private clsDocumentoCotizacion? _documento;
    private clsCotizacionSolicitud? _agregada;

    public clsGenerarPdfServiceTests()
    {
        _usuario = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas Garcia", "nvargas@boterosoto.com.co", null, DateTimeOffset.UtcNow).Valor;
        _usuarios.ObtenerPorIdAsync(_usuario.Id, Arg.Any<CancellationToken>()).Returns(_usuario);
        _margen.ResolverLineaAsync(Arg.Is<string?>(l => l == "Nacional"), Arg.Any<CancellationToken>()).Returns(_nacional);
        _calendario.Hoy.Returns(new DateOnly(2026, 9, 24));
        _generador.GenerarAsync(Arg.Do<clsDocumentoCotizacion>(d => _documento = d), Arg.Any<CancellationToken>()).Returns(new clsPdfGenerado([0x25, 0x50, 0x44, 0x46], 3));
        _almacen.GuardarNuevoAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/data/pdfs/{ci.ArgAt<string>(0)}");
        _cotizaciones.When(c => c.Agregar(Arg.Any<clsCotizacionSolicitud>())).Do(ci => _agregada = ci.Arg<clsCotizacionSolicitud>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsGenerarPdfService Servicio() =>
        new(_usuarios, _cotizaciones, _margen, _generador, _almacen, _unidadTrabajo, _calendario, Options.Create(_opciones), new clsLoggerFalso<clsGenerarPdfService>());

    private clsEntradaGenerarPdf Entrada(clsCotizacionPdfPayload? payload = null, string folio = "PCN-19032026", clsTrazabilidadCotizacion? traza = null) =>
        new(_usuario.Id, payload ?? clsEjemploPdf.Payload(), folio, Trazabilidad: traza, SpaceId: "web/s-1", ThreadId: "web/s-1");

    // test_generar_pdf_dod_via_mcp + test_generar_pdf_persiste_trazabilidad_completa
    [Fact]
    public async Task GeneraGuardaYRegistraLaCotizacionConSuTrazabilidad()
    {
        var traza = new clsTrazabilidadCotizacion(
            Costo: 3_000_000m, MargenPolitica: 0.135m, MargenAplicado: 0.11m, OrigenMargen: "negociado",
            PrecioFinal: 3_370_787m, NitCliente: "900123456", OrigenCliente: "cliente");

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(3_370_787m), traza: traza), Ct);

        var salida = resultado.Valor;
        salida.Folio.Should().Be("PCN-19032026");
        salida.PdfUrl.Should().Be("/pdfs/PCN-19032026.pdf");
        salida.RegistradaEnBd.Should().BeTrue();
        salida.CotizacionId.Should().Be(_agregada!.Id);

        _agregada.Folio.Should().Be("PCN-19032026");
        // HU-021: la línea de negocio, el costo, los márgenes y el método son de la ruta 1 (la del chat).
        var ruta = _agregada.PrimeraRuta!;
        ruta.LineaNegocioId.Should().Be(_nacional.Id);
        _agregada.UsuarioId.Should().Be(_usuario.Id);
        _agregada.ClienteNombre.Should().Be("LONGYIN");
        _agregada.PdfPath.Should().Be("/data/pdfs/PCN-19032026.pdf");
        _agregada.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        _agregada.CostoFinal.Should().Be(3_000_000m);
        ruta.MargenPoliticaSugerido.Should().Be(0.135m);
        ruta.MargenAplicado.Should().Be(0.11m);
        ruta.OrigenMargen.Should().Be(OrigenMargen.Negociado);
        _agregada.PrecioFinal.Should().Be(3_370_787m);
        _agregada.NitCliente.Should().Be("900123456");
        ruta.MetodoCalculo.Should().Be("margen");
        _agregada.Moneda.Should().Be("COP");
        _agregada.SpaceId.Should().Be("web/s-1");
        _agregada.ThreadId.Should().Be("web/s-1");
        await _almacen.Received(1).GuardarNuevoAsync("PCN-19032026.pdf", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaFirmaSaleDeUsuariosWebYElCelularSoloSiHayTelefono()
    {
        await Servicio().GenerarAsync(Entrada(), Ct);
        _documento!.Firma.Should().Be(new clsFirmaDocumento("Nathaly Vargas Garcia", "Ejecutiva Comercial", "nvargas@boterosoto.com.co", null));

        _usuario.CambiarTelefono("313 415 6187");
        await Servicio().GenerarAsync(Entrada(folio: "PCN-19032026-v2"), Ct);
        _documento!.Firma.Celular.Should().Be("313 415 6187");
    }

    [Fact]
    public async Task ElFolioImpresoEsElDeLaEntradaNoLaReferenciaDelPayload()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Referencia = "PCN-INVENTADO-POR-EL-LLM";

        var resultado = await Servicio().GenerarAsync(Entrada(payload, folio: "PCN-NV-24092026-001"), Ct);

        resultado.Valor.Folio.Should().Be("PCN-NV-24092026-001");
        _documento!.Referencia.Should().Be("PCN-NV-24092026-001");
    }

    // test_generar_pdf_sin_referencia_error: ahora el folio lo genera el servidor (ADR-011) y es obligatorio.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task SinFolioDelServidorEsPayloadInvalidoAunqueElPayloadTraigaReferencia(string? folio)
    {
        var resultado = await Servicio().GenerarAsync(Entrada(folio: folio!), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("lo genera el servidor");
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    private static clsCotizacionPdfPayload UnaFila(decimal? tarifa = 3_000_000m, string? moneda = null)
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Filas!.RemoveAt(1);
        payload.Tarifa.Filas[0].Tarifa = tarifa;
        payload.Tarifa.Filas[0].Moneda = moneda;
        return payload;
    }

    // I-1 (ADR-003/ADR-004): lo impreso cuadra con la trazabilidad del estado.
    [Fact]
    public async Task LaTarifaImpresaDebeSerElPrecioFinalDeLaTrazabilidad()
    {
        var traza = new clsTrazabilidadCotizacion(Costo: 2_550_000m, MargenAplicado: 0.15m, PrecioFinal: 3_000_000m);

        (await Servicio().GenerarAsync(Entrada(UnaFila(), traza: traza), Ct)).EsExitoso.Should().BeTrue();

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(3_100_000m), folio: "PCN-2", traza: traza), Ct);
        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("$3.100.000").And.Contain("$3.000.000").And.Contain("ADR-004");
        await _generador.Received(1).GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaTarifaCalculadaConCostoYMargenDelPayloadTambienSeCompara()
    {
        var payload = UnaFila(null);
        payload.Tarifa!.Filas![0].Costo = 2_550_000m;
        payload.Tarifa.Filas[0].Margen = 0.10m; // 2.833.333, no 3.000.000

        var resultado = await Servicio().GenerarAsync(Entrada(payload, traza: new clsTrazabilidadCotizacion(PrecioFinal: 3_000_000m)), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("$2.833.333");
    }

    [Fact]
    public async Task UnPrecioFinalQueNoSaleDeCostoYMargenSeRechaza()
    {
        var traza = new clsTrazabilidadCotizacion(Costo: 2_550_000m, MargenAplicado: 0.15m, PrecioFinal: 3_100_000m);

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(3_100_000m), traza: traza), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("no corresponde al costo y margen");
    }

    [Fact]
    public async Task VariasTarifasDistintasNoPuedenTraerUnSoloPrecioFinal()
    {
        var resultado = await Servicio().GenerarAsync(Entrada(traza: new clsTrazabilidadCotizacion(PrecioFinal: 3_000_000m)), Ct);

        resultado.Error.Mensaje.Should().Contain("$3.600.000");
    }

    // HU-012.3: la moneda de las filas es la de la cotización.
    [Fact]
    public async Task LaMonedaDeLasFilasDebeSerLaDeLaCotizacion()
    {
        var enDolares = await Servicio().GenerarAsync(Entrada(UnaFila(moneda: "USD")), Ct);
        enDolares.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        enDolares.Error.Mensaje.Should().Contain("USD").And.Contain("COP");

        var coherente = await Servicio().GenerarAsync(
            Entrada(UnaFila(moneda: "usd"), folio: "PCN-USD", traza: new clsTrazabilidadCotizacion(Moneda: "USD", TrmAplicada: 3900m, PrecioCop: 11_700_000_000m)), Ct);
        coherente.EsExitoso.Should().BeTrue(coherente.EsFallo ? coherente.Error.Mensaje : string.Empty);
        _documento!.Secciones[0].Filas[0].Tarifa.Should().Be("US$3.000.000,00");
        _agregada!.Moneda.Should().Be("USD");
    }

    // HU-012.2: en USD el precio final es el de pesos (costo y margen) convertido con la TRM de la trazabilidad, a 2 decimales.
    [Fact]
    public async Task EnDolaresElPrecioFinalEsElDePesosConvertidoConLaTrm()
    {
        var traza = new clsTrazabilidadCotizacion(
            Costo: 2_850_000m, MargenAplicado: 0.135m, PrecioFinal: 996.35m, Moneda: "USD", TrmAplicada: 3306.86m, PrecioCop: 3_294_798m);

        var coherente = await Servicio().GenerarAsync(Entrada(UnaFila(996.35m, "USD"), traza: traza), Ct);

        coherente.EsExitoso.Should().BeTrue(coherente.EsFallo ? coherente.Error.Mensaje : string.Empty);
        _agregada!.PrecioFinal.Should().Be(996.35m);
        _agregada.TrmAplicada.Should().Be(3306.86m);
        _agregada.CostoFinal.Should().Be(2_850_000m, "HU-021: el costo del chat es el flete de su camión");
        _documento!.Secciones[0].Filas[0].Tarifa.Should().Be("US$996,35");
        _documento.NotaMoneda.Should().Contain("TRM mínima de $3.306,86", "HU-012.3: la nota usa la TRM aplicada de la trazabilidad")
            .And.NotContain("mínimo a pagar");
    }

    // Coherencia de la conversión (HU-012.2): el valor en pesos de la trazabilidad debe salir del costo y el margen.
    [Fact]
    public async Task UnPrecioCopQueNoSaleDeCostoYMargenSeRechaza()
    {
        var traza = new clsTrazabilidadCotizacion(
            Costo: 2_850_000m, MargenAplicado: 0.135m, PrecioFinal: 996.35m, Moneda: "USD", TrmAplicada: 3306.86m, PrecioCop: 3_300_000m);

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(996.35m, "USD"), traza: traza), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("$3.300.000").And.Contain("$3.294.798");
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    // HU-012.3 (2026-10-05): la nota ya no necesita el valor en pesos; sin PrecioCop ni costo el PDF en USD sale con la TRM aplicada.
    [Fact]
    public async Task SinPrecioCopNiCostoElPdfEnDolaresLlevaLaNotaConLaTrmAplicada()
    {
        var traza = new clsTrazabilidadCotizacion(PrecioFinal: 996.35m, Moneda: "USD", TrmAplicada: 3273.49m);

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(996.35m, "USD"), traza: traza), Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.NotaMoneda.Should().Be(clsTextosEstandar.NotaTrmMinima(3273.49m));
    }

    [Fact]
    public async Task EnDolaresUnPrecioQueNoSaleDeLaTrmSeRechaza()
    {
        var traza = new clsTrazabilidadCotizacion(Costo: 2_850_000m, MargenAplicado: 0.135m, PrecioFinal: 996.36m, Moneda: "USD", TrmAplicada: 3306.86m);

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(996.36m, "USD"), traza: traza), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("no corresponde al costo y margen").And.Contain("US$996,35");
    }

    [Fact]
    public async Task EnDolaresLaTarifaImpresaSeComparaConCentavos()
    {
        var traza = new clsTrazabilidadCotizacion(Costo: 2_850_000m, MargenAplicado: 0.135m, PrecioFinal: 996.35m, Moneda: "USD", TrmAplicada: 3306.86m);

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(996.30m, "USD"), traza: traza), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("US$996,30");
    }

    [Fact]
    public async Task EnDolaresSinTrmLaCotizacionSeRechaza()
    {
        var traza = new clsTrazabilidadCotizacion(PrecioFinal: 996.35m, Moneda: "USD");

        var resultado = await Servicio().GenerarAsync(Entrada(UnaFila(996.35m, "USD"), traza: traza), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("TRM");
        await _generador.DidNotReceiveWithAnyArgs().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnServicioConexoEnOtraMonedaSeRechaza()
    {
        var payload = UnaFila();
        payload.ServiciosConexos = [new clsServicioConexoPayload { Descripcion = "Levante", Precio = 100m, Moneda = "USD" }];

        (await Servicio().GenerarAsync(Entrada(payload), Ct)).Error.Mensaje.Should().Contain("Levante");
    }

    [Theory]
    [InlineData("PCN 19/03/2026")]
    [InlineData("../etc/passwd")]
    [InlineData("PCN-123456789012345678901234567890123456789012345678901")]
    public async Task UnFolioQueNoSirveComoNombreDeArchivoEsPayloadInvalido(string folio)
    {
        var resultado = await Servicio().GenerarAsync(Entrada(folio: folio), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    // test_generar_pdf_linea_negocio_desconocida_hoy_falla_entero (issue #44): ahora se rechaza antes de renderizar.
    [Fact]
    public async Task UnaLineaDesconocidaSeRechazaAntesDeRenderizar()
    {
        var payload = clsEjemploPdf.Payload();
        payload.LineaNegocio = "Marítimo";

        var resultado = await Servicio().GenerarAsync(Entrada(payload), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
        await _almacen.DidNotReceiveWithAnyArgs().GuardarNuevoAsync(default!, default!, Ct);
    }

    // #41 (auditoría de #12, F4): con una línea inactiva no se cotiza.
    [Fact]
    public async Task UnaLineaInactivaSeRechazaAntesDeRenderizar()
    {
        _nacional.Desactivar();

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        resultado.Error.Detalle.Should().Be(clsMargenResolverService.DetalleLineaInactiva);
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    // test_generar_pdf_idempotencia_versiona: en .NET el folio lo genera #12 y la colisión se informa.
    [Fact]
    public async Task UnFolioExistenteEsFolioDuplicadoSinRenderizar()
    {
        _cotizaciones.ExisteFolioAsync("PCN-19032026", Arg.Any<CancellationToken>()).Returns(true);

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.FolioDuplicado);
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiElArchivoYaExisteEnElAlmacenNoSeReemplazaYEsFolioDuplicado()
    {
        _almacen.GuardarNuevoAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.FolioDuplicado);
        _cotizaciones.DidNotReceive().Agregar(Arg.Any<clsCotizacionSolicitud>());
        await _almacen.DidNotReceiveWithAnyArgs().EliminarAsync(default!, Ct);
    }

    [Fact]
    public async Task SiLaBaseRechazaElFolioSeBorraElPdfRecienGuardado()
    {
        _unidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new clsViolacionUnicidadException(
            "uq_cotizaciones_folio", new clsErrorDominio(clsViolacionUnicidadException.CodigoFolioDuplicado, "Ya existe."), null));

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.FolioDuplicado);
        await _almacen.Received(1).EliminarAsync("PCN-19032026.pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiLaBaseFallaSeBorraElPdfYLaExcepcionSePropaga()
    {
        _unidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var generar = () => Servicio().GenerarAsync(Entrada(), Ct);

        await generar.Should().ThrowAsync<InvalidOperationException>();
        await _almacen.Received(1).EliminarAsync("PCN-19032026.pdf", Arg.Any<CancellationToken>());
    }

    // test_generar_pdf_render_no_disponible_error
    [Fact]
    public async Task UnFalloDelRenderEsPdfRenderNoDisponible()
    {
        _generador.GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("sin fuentes"));

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfRenderNoDisponible);
        await _almacen.DidNotReceiveWithAnyArgs().GuardarNuevoAsync(default!, default!, Ct);
    }

    // #92, #95: el render es uno a la vez por proceso; si el turno no llega a tiempo es transitorio (503) y no se guarda nada.
    [Fact]
    public async Task SiElTurnoDeRenderSeAgotaEsPdfRenderNoDisponible()
    {
        _generador.GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new clsRenderPdfOcupadoException(TimeSpan.FromSeconds(30)));

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfRenderNoDisponible);
        resultado.Error.Mensaje.Should().Contain("ocupado");
        await _almacen.DidNotReceiveWithAnyArgs().GuardarNuevoAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SiElComercialNoExisteEsErrorInterno()
    {
        var resultado = await Servicio().GenerarAsync(Entrada() with { UsuarioId = Guid.NewGuid() }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
    }

    [Fact]
    public async Task SinComercialLaLlamadaEsUnErrorDeProgramacion()
    {
        var generar = () => Servicio().GenerarAsync(Entrada() with { UsuarioId = Guid.Empty }, Ct);

        await generar.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("inventado", null, null)]
    [InlineData(null, "empresa", null)]
    [InlineData(null, null, "1.2")]
    public async Task UnaTrazabilidadInvalidaEsPayloadInvalidoAntesDeRenderizar(string? origenMargen, string? origenCliente, string? margenAplicado)
    {
        var traza = new clsTrazabilidadCotizacion(
            OrigenMargen: origenMargen,
            OrigenCliente: origenCliente,
            MargenAplicado: margenAplicado is null ? null : decimal.Parse(margenAplicado, System.Globalization.CultureInfo.InvariantCulture));

        var resultado = await Servicio().GenerarAsync(Entrada(traza: traza), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        await _generador.DidNotReceive().GenerarAsync(Arg.Any<clsDocumentoCotizacion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConBaseUrlLaUrlDelPdfEsAbsoluta()
    {
        _opciones.BaseUrl = "https://cotifly.boterosoto.com.co/";

        var resultado = await Servicio().GenerarAsync(Entrada(), Ct);

        resultado.Valor.PdfUrl.Should().Be("https://cotifly.boterosoto.com.co/pdfs/PCN-19032026.pdf");
    }

    [Fact]
    public async Task SinFechaEnElPayloadElDocumentoUsaLaFechaDeNegocio()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Fecha = null;

        await Servicio().GenerarAsync(Entrada(payload), Ct);

        _documento!.FechaLarga.Should().Be("Yumbo, 24 de septiembre de 2026");
    }
}
