using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Aplicacion.Redaccion.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Textos del PDF del costeo (HU-013.5, ADR-018 §2a, §8 y §11): redactar con la IA (con respaldo), editar como comercial, la regla del
/// PDF, el control de versión, <c>TEXTOS_EDITADOS</c> con su sobrescritura y el PDF que imprime los textos guardados.
/// </summary>
public sealed class clsTextosCosteoServiceTests
{
    private const int Version = 7;
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsRedaccionCotizacionService _redaccion = Substitute.For<IClsRedaccionCotizacionService>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacion _cotizacion;

    public clsTextosCosteoServiceTests()
    {
        _cotizacion = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-001");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ListarLineasAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns([]);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _redaccion.RedactarAsync(_cotizacion.Folio, Arg.Any<clsContextoRedaccion>(), Arg.Any<CancellationToken>())
            .Returns(new clsResultadoRedaccion("Cotización transporte Buenaventura - Bogotá", "Apreciado cliente.\n\nCon gusto.",
                clsTextosCotizacion.FuenteIa, "guion", "modelo", 120, false, null));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RedactarGuardaLosTextosDeLaIaSinEditarYSubeLaVersion()
    {
        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, sobrescribir: false, Version, Ct);

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

        var sin = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, sobrescribir: false, Version, Ct);

        sin.Error.Codigo.Should().Be(clsCodigosError.TextosEditados);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
        clsTextosCotizacionJson.Leer(_cotizacion.TextosCotizacion)!.Asunto.Should().Be("Mi asunto");

        var con = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, sobrescribir: true, Version, Ct);

        con.EsExitoso.Should().BeTrue();
        con.Valor.Textos.Editado.Should().BeFalse();
        con.Valor.Textos.Asunto.Should().Be("Cotización transporte Buenaventura - Bogotá");
    }

    [Fact]
    public async Task YaEnviadaNoSeRedactaNiSeEditaYNoSeLlamaALaIa()
    {
        _cotizacion.RegistrarEnvio();

        var redactar = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, Ct);
        var editar = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Intro", Version, Ct);

        redactar.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        redactar.Error.Datos!["accion"].Should().Be("nueva_version");
        editar.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
        _cotizacion.TextosCotizacion.Should().BeNull();
    }

    [Fact]
    public async Task UnaVersionDesactualizadaRespondeConflictoSinLlamarALaIa()
    {
        var redactar = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version - 1, Ct);
        var editar = await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", "Intro", Version - 1, Ct);

        redactar.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        editar.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _redaccion.DidNotReceiveWithAnyArgs().RedactarAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SiLaCotizacionCambioMientrasLaIaRedactabaNoSeGuardaNada()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    [Fact]
    public async Task UnaCotizacionAjenaEs404()
    {
        var resultado = await Servicio().RedactarAsync(Guid.NewGuid(), _cotizacion.Folio, false, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    [Fact]
    public async Task ElComercialEditaLosTextosConservaLaFuenteYPuedeEscribirMontos()
    {
        await Servicio().RedactarAsync(_ana, _cotizacion.Folio, false, Version, Ct);
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
    public async Task LargosDelComercialPorEncimaDe120Y900SeRechazan()
    {
        (await Servicio().EditarAsync(_ana, _cotizacion.Folio, new string('a', 121), "Intro", Version, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.TextosCotizacionInvalidos);
        (await Servicio().EditarAsync(_ana, _cotizacion.Folio, "Asunto", new string('b', 901), Version, Ct)).Error.Codigo
            .Should().Be(clsCodigosError.TextosCotizacionInvalidos);
        (await Servicio().EditarAsync(_ana, _cotizacion.Folio, new string('a', 120), new string('b', 900), Version, Ct)).EsExitoso
            .Should().BeTrue();
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
        var contexto = clsTextosCosteoService.Contexto(_cotizacion, "Nacional", [], "Ana Pérez");

        typeof(clsContextoRedaccion).GetProperties().Select(p => p.PropertyType)
            .Should().OnlyContain(t => t == typeof(string) || t == typeof(IReadOnlyList<clsRutaRedaccion>) || t == typeof(IReadOnlyList<string>),
                "el contexto de la IA no tiene ningún campo numérico (ADR-018 §2a)");
        contexto.Conexos.Should().BeEmpty("el único conexo de la cotización está excluido");
        contexto.LineaNegocio.Should().Be("Nacional");
        contexto.Comercial.Should().Be("Ana Pérez");
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
        clsTextosEstandar.IntroduccionEstandar.Length.Should().BeLessThanOrEqualTo(clsTextosCotizacion.LargoMaximoIntroduccion);
        clsTextosEstandar.AsuntoEstandar("Nacional").Should().Be("presentación oferta de servicios de Transporte Nacional");
        clsReglasTextosCotizacion.Validar(clsTextosEstandar.AsuntoEstandar("Nacional"), clsTextosEstandar.IntroduccionEstandar, permitirMontos: false)
            .EsValida.Should().BeTrue("el respaldo cumple las mismas reglas que la IA");
    }

    private clsTextosCosteoService Servicio() =>
        new(_cotizaciones, _politicas, _usuarios, _redaccion, _unidad, _auditoria, TimeProvider.System);
}
