using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>"Mis cotizaciones" (resumen con códigos y <c>pdf_url</c>) y los datos para una versión nueva.</summary>
public sealed class clsMisCotizacionesYNuevaVersionTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly Guid _ana = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsCotizacionSolicitud Cotizacion(string folio, int version = 1)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            Version = version,
            UsuarioId = _ana,
            NitCliente = "900123456",
            ClienteNombre = "Cliente SA",
            OrigenCliente = OrigenCliente.Cliente,
            PdfPath = $"/data/pdfs/{folio}.pdf",
        }).Valor;
        // HU-021: la línea de negocio, el costo, el margen y el método son de la ruta 1.
        cotizacion.RegistrarCotizacionChat(1, 100m, 0.15m, 0.1m, OrigenMargen.Negociado, "markup", 110m);
        return cotizacion;
    }

    [Fact]
    public async Task ElListadoResumeConCodigosLineaYUrlDelPdf()
    {
        var cotizacion = Cotizacion("PCN-DG-25092026-001");
        _cotizaciones.ListarConLineaPorUsuarioAsync(_ana, 100, Arg.Any<CancellationToken>())
            // HU-021: el listado no carga el agregado; el repositorio trae el margen y su origen de la ruta 1.
            .Returns([new clsCotizacionConLinea(cotizacion, "Nacional", 0.1m, OrigenMargen.Negociado)]);
        var servicio = new clsMisCotizacionesService(_cotizaciones, _politicas, Options.Create(new clsOpcionesPdf { BaseUrl = "https://api.cotifly/" }));

        var lista = await servicio.ListarAsync(_ana, Ct);

        lista.Total.Should().Be(1);
        var fila = lista.Cotizaciones.Should().ContainSingle().Subject;
        fila.Should().BeEquivalentTo(new
        {
            cotizacion.Id,
            Folio = "PCN-DG-25092026-001",
            Version = 1,
            NitCliente = "900123456",
            OrigenCliente = "cliente",
            OrigenMargen = "negociado",
            Cliente = "Cliente SA",
            LineaNegocio = "Nacional",
            PrecioFinal = 110m,
            MargenAplicado = 0.1m,
            Moneda = "COP",
            EstadoEnvio = "sin_enviar",
            PdfUrl = "https://api.cotifly/pdfs/PCN-DG-25092026-001.pdf",
        });
    }

    [Fact]
    public async Task SinUsuarioElListadoEstaVacioSinConsultar()
    {
        var servicio = new clsMisCotizacionesService(_cotizaciones, _politicas, Options.Create(new clsOpcionesPdf()));

        var lista = await servicio.ListarAsync(Guid.Empty, Ct);

        lista.Total.Should().Be(0);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ListarConLineaPorUsuarioAsync(default, default, Ct);
    }

    [Fact]
    public void SinPdfLaUrlEsNulaYSinBaseEsRelativa()
    {
        clsUrlPdf.DesdeReferencia(null, null).Should().BeNull();
        clsUrlPdf.DesdeReferencia("", "/data/pdfs/PCN-1.pdf").Should().Be("/pdfs/PCN-1.pdf");
        clsUrlPdf.DesdeReferencia(null, @"C:\pdfs\PCN-2.pdf").Should().Be("/pdfs/PCN-2.pdf");
    }

    [Fact]
    public async Task LosDatosDeNuevaVersionTraenLaBaseYLaSiguienteDeLaFamilia()
    {
        var v2 = Cotizacion("PCN-DG-25092026-001-v2", 2);
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(v2.Id, _ana, Arg.Any<CancellationToken>()).Returns(v2);
        _cotizaciones.ObtenerVersionMaximaAsync("PCN-DG-25092026-001", Arg.Any<CancellationToken>()).Returns(3);
        _politicas.ObtenerPorIdAsync(1, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Nacional", 0.15m).Valor);

        var datos = (await new clsNuevaVersionService(_cotizaciones, _politicas).ObtenerDatosAsync(_ana, v2.Id, Ct)).Valor;

        datos.Should().BeEquivalentTo(new
        {
            CotizacionOrigenId = v2.Id,
            FolioBase = "PCN-DG-25092026-001",
            VersionSiguiente = 4,
            ClienteNombre = "Cliente SA",
            OrigenCliente = "cliente",
            LineaNegocio = "Nacional",
            Costo = 100m,
            OrigenMargen = "negociado",
            MetodoCalculo = "markup",
            Moneda = "COP",
        });
    }

    // --- HU-021.9 (#136): "Mis cotizaciones" con varias líneas de negocio; el margen y su origen salen de la ruta ---

    /// <summary>Licitación de dos rutas costeadas: la ruta 1 con la línea 3 (margen ajustado a 0,2) y la ruta 2 con la línea 7.</summary>
    private clsCotizacionSolicitud DeDosLineas()
    {
        var cotizacion = Cotifly.Pruebas.Comun.clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], folio: "PCN-DG-25092026-002", usuarioId: _ana);
        typeof(clsLineaSolicitud).GetProperty(nameof(clsLineaSolicitud.LineaNegocioId))!.SetValue(cotizacion.RutasOrdenadas[1], 7);
        cotizacion.AplicarMargenNegociado(1, 0.2m, DateTimeOffset.UtcNow).EsExitoso.Should().BeTrue();
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Nacional", 0.15m).Valor);
        _politicas.ObtenerPorIdAsync(7, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Contenedores", 0.12m).Valor);
        return cotizacion;
    }

    [Fact]
    public async Task ConRutasDeVariasLineasElResumenDiceVariasYElMargenEsElDeLaRuta1()
    {
        var cotizacion = DeDosLineas();
        var servicio = new clsMisCotizacionesService(_cotizaciones, _politicas, Options.Create(new clsOpcionesPdf()));

        var resumen = await servicio.ResumirAsync(cotizacion, Ct);

        resumen.LineaNegocio.Should().Be(clsCotizacionConLinea.Varias);
        (resumen.MargenAplicado, resumen.OrigenMargen).Should().Be((0.2m, "negociado"), "el margen y su origen son los de la ruta 1");
        resumen.PrecioFinal.Should().Be(cotizacion.Rutas.Sum(r => r.PrecioFinalLinea!.Value), "el total es la suma de las rutas");
    }

    [Fact]
    public async Task ConRutasDeUnaSolaLineaElResumenTraeEsaLinea()
    {
        var cotizacion = Cotifly.Pruebas.Comun.clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m], folio: "PCN-DG-25092026-003", usuarioId: _ana);
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Nacional", 0.15m).Valor);
        var servicio = new clsMisCotizacionesService(_cotizaciones, _politicas, Options.Create(new clsOpcionesPdf()));

        (await servicio.ResumirAsync(cotizacion, Ct)).LineaNegocio.Should().Be("Nacional");
    }

    [Fact]
    public async Task LosDatosDeNuevaVersionTraenCadaRutaConSuLineaYSuMargen()
    {
        var cotizacion = DeDosLineas();
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacion.Id, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);

        var datos = (await new clsNuevaVersionService(_cotizaciones, _politicas).ObtenerDatosAsync(_ana, cotizacion.Id, Ct)).Valor;

        datos.LineaNegocio.Should().Be(clsCotizacionConLinea.Varias);
        datos.MargenAplicado.Should().Be(0.2m);
        datos.Rutas.Should().HaveCount(2);
        datos.Rutas[0].Should().BeEquivalentTo(new { LineaOrden = 1, LineaNegocioId = 3, LineaNegocio = "Nacional", MargenAplicado = 0.2m, OrigenMargen = "negociado" });
        datos.Rutas[1].Should().BeEquivalentTo(new { LineaOrden = 2, LineaNegocioId = 7, LineaNegocio = "Contenedores", MargenAplicado = 0.15m, OrigenMargen = "politica_linea" });
    }

    [Fact]
    public async Task UnaCotizacionAjenaNoTraeDatosDeNuevaVersion()
    {
        var resultado = await new clsNuevaVersionService(_cotizaciones, _politicas).ObtenerDatosAsync(_ana, Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }
}
