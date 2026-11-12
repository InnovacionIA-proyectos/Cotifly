using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Aplicacion.Redaccion.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Textos del PDF del costeo (ADR-018 §2a, §8 y §11): redactar con la IA (con respaldo), editar como comercial, la regla del
/// PDF, el control de versión, <c>TEXTOS_EDITADOS</c> con su sobrescritura y el PDF que imprime los textos guardados.
/// </summary>
public sealed class clsTextosCosteoServiceTests
{
    private const int Version = 7;
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsRedaccionCotizacionService _redaccion = Substitute.For<IClsRedaccionCotizacionService>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsTextosCosteoServiceTests()
    {
        // Los textos del PDF se redactan con la cotización finalizada.
        _cotizacion = clsAjusteMargenCosteoServiceTests.Finalizada(_ana, "PCN-900555111-AP-05102026-001");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _redaccion.VersionPrompt.Returns("abcdef0123456789");
        _redaccion.RedactarAsync(Arg.Any<clsContextoRedaccion>(), Arg.Any<clsTextosBase>(), Arg.Any<CancellationToken>())
            .Returns(new clsResultadoRedaccion("Cotización transporte Buenaventura - Bogotá", "Apreciado cliente.\n\nCon gusto.",
                clsTextosCotizacion.FuenteIa, "guion", "modelo", "abcdef0123456789", 120, false, null));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RedactarGuardaLosTextosDeLaIaSinEditarYSubeLaVersion()
    {
        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, sobrescribir: false, Version, cancellationToken: Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Textos.Fuente.Should().Be("ia");
        resultado.Valor.Textos.Editado.Should().BeFalse();
        resultado.Valor.RowVersion.Should().Be("8");
        clsTextosCotizacionJson.Leer(_cotizacion.TextosCotizacion)!.Asunto.Should().Be("Cotización transporte Buenaventura - Bogotá");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConTextosEditadosRedactarResponde409SinLlamarALaIaSalvoConSobrescribir()
    {
        _cotizacion.RegistrarTextos(clsTextosCotizacionJson.Escribir(
            new clsTextosCotizacion("Mi asunto", "Mi introducción.", clsTextosCotizacion.FuenteIa, true, DateTimeOffset.UtcNow)));

        var sin = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, sobrescribir: false, Version, cancellationToken: Ct);

        sin.Error.Codigo.Should().Be(clsCodigosError.TextosEditados);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
        clsTextosCotizacionJson.Leer(_cotizacion.TextosCotizacion)!.Asunto.Should().Be("Mi asunto");

        var con = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, sobrescribir: true, Version, cancellationToken: Ct);

        con.EsExitoso.Should().BeTrue();
        con.Valor.Textos.Editado.Should().BeFalse();
        con.Valor.Textos.Asunto.Should().Be("Cotización transporte Buenaventura - Bogotá");
    }

    [Fact]
    public async Task YaEnviadaNoSeRedactaNiSeEditaYNoSeLlamaALaIa()
    {
        _cotizacion.RegistrarEnvio();

        var redactar = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);
        var editar = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Intro", Version, Ct);

        redactar.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        redactar.Error.Datos!["accion"].Should().Be("nueva_version");
        editar.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
        _cotizacion.TextosCotizacion.Should().BeNull();
    }

    [Fact]
    public async Task SinFinalizarNoSeRedactaNiSeEditaYNoSeLlamaALaIa()
    {
        var sinFinalizar = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-077");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(sinFinalizar.Folio, _ana, Arg.Any<CancellationToken>()).Returns(sinFinalizar);

        var redactar = await Servicio().RedactarAsync(_ana, sinFinalizar.Folio, false, Version, cancellationToken: Ct);
        var editar = await Servicio().EditarAsync(_ana, sinFinalizar.Folio, "Asunto", "Intro", Version, Ct);

        redactar.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        redactar.Error.Mensaje.Should().StartWith($"Finaliza la cotización {sinFinalizar.Folio} antes de redactar los textos del PDF");
        redactar.Error.Datos!["endpoint"].Should().Be($"/web/cotizaciones/{sinFinalizar.Folio}/finalizar");
        editar.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
        sinFinalizar.TextosCotizacion.Should().BeNull();
    }

    [Fact]
    public async Task FinalizadaYSinEnviarLosTextosSiSeGuardan()
    {
        _cotizacion.Finalizada.Should().BeTrue();

        var editar = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Introducción", Version, Ct);

        editar.EsExitoso.Should().BeTrue("finalizar no bloquea los textos: el PDF se redacta después de aceptar el precio");
    }

    [Fact]
    public async Task UnaVersionDesactualizadaRespondeConflictoSinLlamarALaIa()
    {
        var redactar = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version - 1, cancellationToken: Ct);
        var editar = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Intro", Version - 1, Ct);

        redactar.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        editar.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SiLaCotizacionCambioMientrasLaIaRedactabaNoSeGuardaNada()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    [Fact]
    public async Task UnaCotizacionAjenaEs404()
    {
        var resultado = await Servicio().RedactarAsync(Guid.NewGuid(), _cotizacion.Folio, false, Version, cancellationToken: Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    [Fact]
    public async Task ElComercialEditaLosTextosConservaLaFuenteYPuedeEscribirMontos()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>()).Returns(true);

        var resultado = await Servicio().EditarAsync(_ana, _cotizacion.Folio, " Propuesta para Cliente SAS ",
            "Apreciado cliente:\r\n\r\nLe ofrecemos una tarifa de $ 3.000.000 por viaje.", Version + 1, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Textos.Should().Match<clsTextosCotizacion>(t =>
            t.Asunto == "Propuesta para Cliente SAS" && t.Introduccion == "Apreciado cliente:\n\nLe ofrecemos una tarifa de $ 3.000.000 por viaje."
            && t.Fuente == "ia" && t.Editado);
        resultado.Valor.RowVersion.Should().Be("9");
        await _auditoria.Received(1).RegistrarAsync(clsTextosCosteoService.HerramientaEdicion, Arg.Any<object?>(),
            Arg.Is<object?>(s => !clsAuditoriaService.AJson(s!).Contains("3.000.000")), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task SinTextosPreviosLaEdicionQuedaConFuenteEstandar()
    {
        var resultado = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Introducción.", Version, Ct);

        resultado.Valor.Textos.Fuente.Should().Be("estandar");
        resultado.Valor.Textos.Editado.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Introducción válida")]
    [InlineData("Asunto válido", "   ")]
    [InlineData("Asunto válido", "Visite https://ejemplo.com para más detalles")]
    [InlineData("Escriba a a@ejemplo.com", "Introducción válida")]
    public async Task TextosDelComercialInvalidosSeRechazanSinGuardar(string asunto, string introduccion)
    {
        var resultado = await Servicio().EditarAsync(_ana, _cotizacion.Folio, asunto, introduccion, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TextosCotizacionInvalidos);
        _cotizacion.TextosCotizacion.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task LargosDelComercialPorEncimaDe120Y1100SeRechazan()
    {
        (await Servicio().EditarAsync(_ana, _cotizacion.Folio, new string('a', 121), "Intro", Version, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.TextosCotizacionInvalidos);
        (await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", new string('b', 1101), Version, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.TextosCotizacionInvalidos);
        (await Servicio().EditarAsync(_ana, _cotizacion.Folio, new string('a', 120), new string('b', 1100), Version, Ct)).EsExitoso
            .Should().BeTrue("el comercial escribe hasta 1100 (la IA, hasta 900)");
    }

    [Fact]
    public async Task ElComercialGuardaLaIntroduccionEstandarDelPliegoSinRecortarla()
    {
        var intro = clsTextosEstandar.IntroduccionEstandar(true);
        intro.Length.Should().BeGreaterThan(clsTextosCotizacion.LargoMaximoIntroduccion).And.BeLessThanOrEqualTo(clsTextosCotizacion.LargoMaximoIntroduccionComercial);

        var resultado = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto del pliego", intro, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Textos.Introduccion.Should().Be(intro);
    }

    [Fact]
    public void LaIaSigueLimitadaA900Caracteres()
    {
        clsReglasTextosCotizacion.Validar("Asunto", new string('b', 901), permitirMontos: false).Motivo.Should().Be(clsReglasTextosCotizacion.IntroduccionLarga);
        clsReglasTextosCotizacion.Validar("Asunto", new string('b', 901), permitirMontos: true).EsValida.Should().BeTrue();
    }

    [Fact]
    public async Task ConElPdfGeneradoSinEnviarEditarLoInvalidaYLoDejaEnElHistorial()
    {
        _cotizacion.AsignarPdf("almacen://PCN-900555111-AP-05102026-001.pdf");

        var resultado = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Propuesta revisada", "Introducción.", Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.PdfPath.Should().BeNull();
        _cotizacion.PdfDesactualizado.Should().BeTrue();
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.Textos));
    }

    [Fact]
    public void ElContextoDeLaIaSoloTraeNombresYNuncaMontos()
    {
        var contexto = clsTextosCosteoService.Contexto(_cotizacion, "Nacional");

        typeof(clsContextoRedaccion).GetProperties().Select(p => p.PropertyType)
            .Should().OnlyContain(t => t == typeof(string) || t == typeof(IReadOnlyList<clsRutaRedaccion>) || t == typeof(IReadOnlyList<string>),
                "el contexto de la IA no tiene ningún campo numérico (ADR-018 §2a)");
        // No hay conexos excluidos: el conexo de la ruta va en el precio y en el PDF, así que la IA ve su nombre.
        contexto.Conexos.Should().Equal("Cargue");
        contexto.LineaNegocio.Should().Be("Nacional");
    }

    [Fact]
    public void ElPdfImprimeLosTextosDeLaIaOEditadosYNoLosEstandarSinEditar()
    {
        var payload = new clsCotizacionPdfPayload { Asunto = null };
        _cotizacion.RegistrarTextos(clsTextosCotizacionJson.Escribir(
            new clsTextosCotizacion("Asunto estándar", "Intro estándar.", clsTextosCotizacion.FuenteEstandar, false, DateTimeOffset.UtcNow)));

        clsCosteoCotizacionService.AplicarTextos(payload, _cotizacion);

        payload.Asunto.Should().BeNull("los estándar sin editar dejan los del PDF de su variante");
        payload.Textos.Should().BeNull();

        _cotizacion.RegistrarTextos(clsTextosCotizacionJson.Escribir(
            new clsTextosCotizacion("Propuesta para Cliente SAS", "Párrafo uno.\n\nPárrafo dos.", clsTextosCotizacion.FuenteIa, false, DateTimeOffset.UtcNow)));

        clsCosteoCotizacionService.AplicarTextos(payload, _cotizacion);

        payload.Asunto.Should().Be("Propuesta para Cliente SAS");
        payload.Textos!.Intro.Should().Equal("Párrafo uno.", "Párrafo dos.");
        payload.Textos.Seguro.Should().BeNull("las condiciones y notas legales siguen fijas");
    }

    [Fact]
    public void LaVistaDelCosteoTraeLosTextosGuardados()
    {
        clsCosteoCotizacionService.Vista(_cotizacion, null).Textos.Should().BeNull();
        _cotizacion.RegistrarTextos(clsTextosCotizacionJson.Escribir(
            new clsTextosCotizacion("Asunto", "Intro.", clsTextosCotizacion.FuenteIa, true, DateTimeOffset.UtcNow)));

        clsCosteoCotizacionService.Vista(_cotizacion, null).Textos!.Editado.Should().BeTrue();
    }

    [Fact]
    public void ElTextoEstandarCabeEnLosLimitesDelContrato()
    {
        clsTextosEstandar.IntroduccionEstandar(false).Length.Should().BeLessThanOrEqualTo(clsTextosCotizacion.LargoMaximoIntroduccion);
        clsTextosEstandar.AsuntoEstandar("Nacional").Should().Be("presentación oferta de servicios de Transporte Nacional");
        clsReglasTextosCotizacion.Validar(clsTextosEstandar.AsuntoEstandar("Nacional"), clsTextosEstandar.IntroduccionEstandar(false), permitirMontos: false)
            .EsValida.Should().BeTrue("el respaldo de tarifa única cumple las mismas reglas que la IA");
    }

    // Indicaciones del comercial para el redactor con IA.

    [Fact]
    public async Task LasIndicacionesLleganALaIaRecortadas()
    {
        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version,
            "  Destacar la experiencia en carga refrigerada, tono cercano.  ", Ct);

        resultado.EsExitoso.Should().BeTrue();
        await _redaccion.Received(1).RedactarAsync(
            Arg.Is<clsContextoRedaccion>(c => c.IndicacionesComercial == "Destacar la experiencia en carga refrigerada, tono cercano."),
            Arg.Any<clsTextosBase>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinCuerpoElContextoEsElDeSiempreSinIndicaciones()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);

        await _redaccion.Received(1).RedactarAsync(
            Arg.Is<clsContextoRedaccion>(c => c.IndicacionesComercial == null),
            Arg.Any<clsTextosBase>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task VacioOSoloEspaciosEsSinIndicaciones(string indicaciones)
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, indicaciones, Ct);

        await _redaccion.Received(1).RedactarAsync(
            Arg.Is<clsContextoRedaccion>(c => c.IndicacionesComercial == null),
            Arg.Any<clsTextosBase>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MasDe500CaracteresEs400SolicitudInvalidaSinLlamarALaIa()
    {
        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version,
            new string('a', clsContextoRedaccion.LargoMaximoIndicaciones + 1), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
        _cotizacion.TextosCotizacion.Should().BeNull();
    }

    [Fact]
    public async Task Exactamente500CaracteresSeAcepta()
    {
        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version,
            new string('a', clsContextoRedaccion.LargoMaximoIndicaciones), Ct);

        resultado.EsExitoso.Should().BeTrue();
        await _redaccion.Received(1).RedactarAsync(
            Arg.Is<clsContextoRedaccion>(c => c.IndicacionesComercial!.Length == clsContextoRedaccion.LargoMaximoIndicaciones),
            Arg.Any<clsTextosBase>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaAuditoriaRegistraConIndicacionesSiSiNoElTexto()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, "Tono cercano y directo.", Ct);

        await _auditoria.Received(1).RegistrarAsync(clsRedaccionCotizacionService.HerramientaAuditoria,
            Arg.Is<object?>(e => clsAuditoriaService.AJson(e!).Contains("\"con_indicaciones\":true") && !clsAuditoriaService.AJson(e!).Contains("Tono cercano")),
            Arg.Any<object?>(), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task SinIndicacionesLaAuditoriaRegistraConIndicacionesFalse()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);

        await _auditoria.Received(1).RegistrarAsync(clsRedaccionCotizacionService.HerramientaAuditoria,
            Arg.Is<object?>(e => clsAuditoriaService.AJson(e!).Contains("\"con_indicaciones\":false")),
            Arg.Any<object?>(), true, Arg.Any<int>(), null);
    }

    private clsTextosCosteoService Servicio() =>
        new(_cotizaciones, _politicas, _redaccion, _unidad, _auditoria, TimeProvider.System);

    [Fact]
    public async Task ElRespaldoQueRecibeElRedactorEsElDeLaVarianteDelPdf()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);

        await _redaccion.Received(1).RedactarAsync(Arg.Any<clsContextoRedaccion>(),
            new clsTextosBase("presentación oferta de servicios de Transporte", clsTextosEstandar.IntroduccionEstandar(false)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EnUnPliegoElRespaldoEsElAsuntoDeLicitacionYLaIntroduccionMultiseccionComoLosImprimeElPdf()
    {
        // El pliego es la solicitud con varias rutas (o una licitación).
        var pliego = clsCotizacionMuestra.Licitacion(2, usuarioId: _ana);

        var estandar = clsCosteoCotizacionService.TextosEstandar(pliego, "Nacional");

        var payload = clsCosteoCotizacionService.PayloadPliego(pliego, "Nacional", "COP");
        estandar.Asunto.Should().Be(payload.Asunto, "el mismo asunto que imprime el pliego");
        estandar.Introduccion.Should().Be(string.Join("\n\n", clsTextosEstandar.MultiSeccion.Intro), "la introducción del pliego, no la de tarifa única");
        clsCosteoCotizacionService.TextosEstandar(_cotizacion, "Nacional")
            .Should().Be(new clsTextosBase("presentación oferta de servicios de Transporte Nacional", string.Join("\n\n", clsTextosEstandar.TarifaUnica.Intro)));
    }

    /// <summary>
    /// La línea de negocio, las rutas y los vehículos salen de las rutas y sus camiones, sin respaldo a la cabecera: con
    /// rutas de dos líneas la IA recibe las dos, y el estándar (pliego) no lleva línea. Nunca montos.
    /// </summary>
    [Fact]
    public async Task LaIaRecibeLasLineasDeNegocioYLosVehiculosDeLasRutas()
    {
        var pliego = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], folio: "PCN-900555111-AP-05102026-030", usuarioId: _ana);
        pliego.Finalizar(_ana, clsCotizacionMuestra.Ahora);
        typeof(clsLineaSolicitud).GetProperty(nameof(clsLineaSolicitud.LineaNegocioId))!.SetValue(pliego.RutasOrdenadas[1], 7);
        clsCotizacionMuestra.ConVersionNegocio(pliego, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(pliego.Folio, _ana, Arg.Any<CancellationToken>()).Returns(pliego);
        _unidad.GuardarCambiosSiVersionAsync(pliego.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(Cotifly.Dominio.Margen.clsMargenPolitica.Crear("Nacional", 0.15m).Valor);
        _politicas.ObtenerPorIdAsync(7, Arg.Any<CancellationToken>()).Returns(Cotifly.Dominio.Margen.clsMargenPolitica.Crear("Contenedores", 0.12m).Valor);

        (await Servicio().RedactarAsync(_ana, pliego.Folio, false, Version, cancellationToken: Ct)).EsExitoso.Should().BeTrue();

        await _redaccion.Received(1).RedactarAsync(
            Arg.Is<clsContextoRedaccion>(c => c.LineaNegocio == "Nacional, Contenedores" && c.TipoVehiculo == "3S3" && c.Rutas.Count == 2),
            new clsTextosBase(clsTextosEstandar.AsuntoLicitacion(null, 2), string.Join("\n\n", clsTextosEstandar.MultiSeccion.Intro)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaRedaccionSeAuditaDespuesDeGuardarConLaHuellaYLaVersionDelPrompt()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);

        var huella = clsRedaccionCotizacionService.Huella("Cotización transporte Buenaventura - Bogotá", "Apreciado cliente.\n\nCon gusto.");
        huella.Should().HaveLength(64);
        await _auditoria.Received(1).RegistrarAsync(clsRedaccionCotizacionService.HerramientaAuditoria,
            Arg.Is<object?>(e => clsAuditoriaService.AJson(e!).Contains(_cotizacion.Folio)),
            Arg.Is<object?>(s => clsAuditoriaService.AJson(s!).Contains("\"guardado\":true") && clsAuditoriaService.AJson(s!).Contains(huella)
                                 && clsAuditoriaService.AJson(s!).Contains("\"version_prompt\":\"abcdef0123456789\"")
                                 && clsAuditoriaService.AJson(s!).Contains("\"respaldo\":false")
                                 && !clsAuditoriaService.AJson(s!).Contains("Apreciado")),
            true, 120, null);
    }

    [Fact]
    public async Task ConConflictoLaAuditoriaDiceQueNoSeGuardo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, cancellationToken: Ct);

        await _auditoria.Received(1).RegistrarAsync(clsRedaccionCotizacionService.HerramientaAuditoria, Arg.Any<object?>(),
            Arg.Is<object?>(s => clsAuditoriaService.AJson(s!).Contains("\"guardado\":false")), false, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task LaEdicionSeAuditaConLaHuellaYLaVersionDelPrompt()
    {
        await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Introducción.", Version, Ct);

        var huella = clsRedaccionCotizacionService.Huella("Asunto", "Introducción.");
        await _auditoria.Received(1).RegistrarAsync(clsTextosCosteoService.HerramientaEdicion, Arg.Any<object?>(),
            Arg.Is<object?>(s => clsAuditoriaService.AJson(s!).Contains(huella) && clsAuditoriaService.AJson(s!).Contains("abcdef0123456789")),
            true, Arg.Any<int>(), null);
    }
}
