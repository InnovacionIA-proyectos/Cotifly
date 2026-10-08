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

    private clsCotizacionSolicitud Cotizacion(string folio, int version = 1) => clsCotizacionSolicitud.Crear(new clsDatosCotizacion
    {
        Folio = folio,
        Version = version,
        UsuarioId = _ana,
        LineaNegocioId = 1,
        NitCliente = "900123456",
        ClienteNombre = "Cliente SA",
        OrigenCliente = OrigenCliente.Cliente,
        OrigenMargen = OrigenMargen.Negociado,
        MetodoCalculo = "markup",
        Costo = 100m,
        MargenPolitica = 0.15m,
        MargenAplicado = 0.1m,
        PrecioFinal = 110m,
        PdfPath = $"/data/pdfs/{folio}.pdf",
    }).Valor;

    [Fact]
    public async Task ElListadoResumeConCodigosLineaYUrlDelPdf()
    {
        var cotizacion = Cotizacion("PCN-DG-25092026-001");
        _cotizaciones.ListarConLineaPorUsuarioAsync(_ana, 100, Arg.Any<CancellationToken>())
            .Returns([new clsCotizacionConLinea(cotizacion, "Nacional")]);
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

    [Fact]
    public async Task UnaCotizacionAjenaNoTraeDatosDeNuevaVersion()
    {
        var resultado = await new clsNuevaVersionService(_cotizaciones, _politicas).ObtenerDatosAsync(_ana, Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }
}
