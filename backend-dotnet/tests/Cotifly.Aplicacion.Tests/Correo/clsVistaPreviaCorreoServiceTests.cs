using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Correo;

/// <summary>Vista previa (<c>VistaPreviaCorreo</c>) y destinatarios previos por cliente (<c>Destinatarios</c>), sin enviar nada.</summary>
public sealed class clsVistaPreviaCorreoServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCorreoRepository _correos = Substitute.For<IClsCorreoRepository>();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsVistaPreviaCorreoServiceTests()
    {
        _cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-V1",
            UsuarioId = _usuario,
            NitCliente = "800765432",
            PdfPath = "/data/pdfs/PCN-V1.pdf",
        }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-V1", _usuario, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        var previo = clsCorreo.Crear(Guid.NewGuid(), ["compras@cliente.com"], EstadoCorreo.Enviado, cc: ["gerente@cliente.com"]).Valor;
        _correos.ObtenerUltimoEnviadoAClienteAsync(_usuario, "800765432", Arg.Any<CancellationToken>()).Returns(previo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsVistaPreviaCorreoService Servicio() => new(_cotizaciones, new clsDestinatariosPreviosService(_correos));

    [Fact]
    public async Task SinBorradorPrecargaLosDestinatariosDelUltimoEnvioYLaPlantilla()
    {
        var vista = (await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", null, Ct)).Valor;

        vista.Asunto.Should().Be("Cotización PCN-V1 — Botero Soto");
        vista.Cuerpo.Should().Be(clsPlantillaCorreo.CuerpoPorDefecto);
        vista.Para.Should().Equal("compras@cliente.com");
        vista.Cc.Should().Equal("gerente@cliente.com");
        vista.Adjunto.Should().Be("PCN-V1.pdf");
    }

    [Fact]
    public async Task LoQueTraeElBorradorManda()
    {
        var borrador = new clsBorradorCorreo(["otro@cliente.com"], ["copia@cliente.com"], "Asunto propio", "Cuerpo propio");

        var vista = (await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", borrador, Ct)).Valor;

        vista.Should().BeEquivalentTo(new clsVistaPreviaCorreo("Asunto propio", "Cuerpo propio", ["otro@cliente.com"], ["copia@cliente.com"], "PCN-V1.pdf"));
    }

    [Fact]
    public async Task UnaCotizacionAjenaNoTieneVistaPrevia()
    {
        var resultado = await Servicio().PrevisualizarAsync(Guid.NewGuid(), "PCN-V1", null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    [Fact]
    public async Task SinEnviosPreviosLosDestinatariosQuedanVacios()
    {
        var destinatarios = await new clsDestinatariosPreviosService(_correos).ObtenerAsync(_usuario, "999", Ct);

        destinatarios.Para.Should().BeEmpty();
        destinatarios.Cc.Should().BeEmpty();
        (await new clsDestinatariosPreviosService(_correos).ObtenerAsync(_usuario, " ", Ct)).Should().BeSameAs(clsDestinatarios.Vacios);
    }

    [Fact]
    public async Task LosDestinatariosPreviosSonLosDelUltimoEnvioDelComercial()
    {
        var destinatarios = await new clsDestinatariosPreviosService(_correos).ObtenerAsync(_usuario, " 800765432 ", Ct);

        destinatarios.Para.Should().Equal("compras@cliente.com");
        destinatarios.Cc.Should().Equal("gerente@cliente.com");
    }

    [Fact]
    public void LaPlantillaHtmlEscapaElTextoYConvierteLosSaltosDeLinea() =>
        clsPlantillaCorreo.Html("<script>x</script>\r\nhola")
            .Should().Contain("&lt;script&gt;x&lt;/script&gt;<br>hola")
            .And.StartWith("<div style=\"font-family:Arial,Helvetica,sans-serif;color:#1A2A3A");
}
