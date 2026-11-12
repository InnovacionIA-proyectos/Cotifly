using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
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
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly clsOpcionesCorreo _opciones = new() { Habilitado = true, Proteccion = new() { Modo = "ninguno", PermitirEnvioReal = true } };
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

    private clsVistaPreviaCorreoService Servicio(string? textoSeguros = null) =>
        new(_cotizaciones, new clsDestinatariosPreviosService(_correos), _usuarios, Substitute.For<IClsCalendarioNegocio>(),
            Options.Create(_opciones), Options.Create(new clsOpcionesPdf { TextoSeguros = textoSeguros }));

    // La vista previa lleva el texto de seguros de la configuración (el mismo que el envío).
    [Fact]
    public async Task LaVistaPreviaLlevaElTextoDeSegurosDeLaConfiguracion()
    {
        var vista = (await Servicio("Ampara la responsabilidad del transporte.").PrevisualizarAsync(_usuario, "PCN-V1", null, Ct)).Valor;

        vista.CuerpoHtml.Should().Contain(System.Net.WebUtility.HtmlEncode(clsTextosEstandar.TituloSegurosPropuesta))
            .And.Contain("Ampara la responsabilidad del transporte.");
    }

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

    // La vista previa aplica la misma validación que el envío (asunto y direcciones sin caracteres de control, formato
    // estricto y el tope de destinatarios, 10 por defecto), así el comercial ve el error antes de enviar.
    [Theory]
    [InlineData(null, null, "Hola\r\nBcc: intruso@ejemplo.invalid", "CORREO_INVALIDO")]
    [InlineData(null, null, "Hola\0", "CORREO_INVALIDO")]
    [InlineData("cliente@empresa.com\r\nBcc: intruso@ejemplo.invalid", null, null, "CORREO_INVALIDO")]
    [InlineData("cliente@empresa.com", "jefe@empresa.com\n", null, "CORREO_INVALIDO")]
    [InlineData("cliente@empresa.com,intruso@ejemplo.invalid", null, null, "CORREO_INVALIDO")]
    public async Task DEF1027B2_LaVistaPreviaRechazaLoQueElEnvioRechazaria(string? para, string? cc, string? asunto, string codigo)
    {
        var borrador = new clsBorradorCorreo(para is null ? null : [para], cc is null ? null : [cc], asunto, null);

        var resultado = await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", borrador, Ct);

        resultado.Error.Codigo.Should().Be(codigo);
    }

    [Fact]
    public async Task Tope228_LaVistaPreviaRechazaMasDe10Destinatarios()
    {
        var borrador = new clsBorradorCorreo([.. Enumerable.Range(1, 6).Select(i => $"c{i}@cliente.com")],
            [.. Enumerable.Range(1, 5).Select(i => $"k{i}@cliente.com")], null, null);

        var resultado = await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", borrador, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoDemasiadosDestinatarios);
        resultado.Error.Datos!["maximo"].Should().Be(10);
        resultado.Error.Datos["recibidos"].Should().Be(11);
    }

    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(2, 2, true)]
    [InlineData(10, 10, true)]
    public async Task Tope228_LaVistaPreviaUsaElTopeConfigurado(int maximo, int recibidos, bool valida)
    {
        _opciones.MaximoDestinatarios = maximo;
        var borrador = new clsBorradorCorreo([.. Enumerable.Range(1, recibidos).Select(i => $"c{i}@cliente.com")], null, null, null);

        var resultado = await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", borrador, Ct);

        if (valida)
        {
            resultado.EsExitoso.Should().BeTrue();
        }
        else
        {
            resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoDemasiadosDestinatarios);
            resultado.Error.Datos!["maximo"].Should().Be(maximo);
        }
    }

    [Fact]
    public async Task UnaCotizacionAjenaEsNoEncontradaAntesQueUnBorradorInvalido()
    {
        var resultado = await Servicio().PrevisualizarAsync(Guid.NewGuid(), "PCN-V1", new clsBorradorCorreo(null, null, "Hola\r\nBcc: x@y.com", null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    [Fact]
    public async Task LoQueTraeElBorradorManda()
    {
        var borrador = new clsBorradorCorreo(["otro@cliente.com"], ["copia@cliente.com"], "Asunto propio", "Cuerpo propio");

        var vista = (await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", borrador, Ct)).Valor;

        vista.Asunto.Should().Be("Asunto propio");
        vista.Cuerpo.Should().Be("Cuerpo propio");
        vista.Para.Should().Equal("otro@cliente.com");
        vista.Cc.Should().Equal("copia@cliente.com");
        vista.Adjunto.Should().Be("PCN-V1.pdf");
        vista.CuerpoHtml.Should().Contain("Cuerpo propio");
        vista.EntornoPruebas.Should().BeFalse();
        vista.Aviso.Should().BeNull();
    }

    [Fact]
    public async Task EnAmbienteDePruebasLaVistaPreviaAvisaQueElCorreoSeRedirige()
    {
        _opciones.Proteccion = new clsOpcionesProteccionCorreo { Modo = "redirigir", BuzonPruebas = "qa@pruebas.local" };

        var vista = (await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", null, Ct)).Valor;

        vista.EntornoPruebas.Should().BeTrue();
        vista.RedirigidoA.Should().Be("qa@pruebas.local");
        vista.Aviso.Should().Contain("Ambiente de pruebas").And.Contain("qa@pruebas.local");
    }

    /// <summary>
    /// Al redirigir (ADR-023), el correo sale solo al buzón de pruebas, sin CC ni CCO. La vista previa no anuncia
    /// la copia oculta al comercial (<c>copia_oculta = null</c>) y el aviso lo dice; con el envío real sigue anunciándola.
    /// </summary>
    [Fact]
    public async Task AlRedirigirLaVistaPreviaNoAnunciaLaCopiaOcultaQueNoSeEnvia()
    {
        var comercial = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas", "nvargas@boterosoto.com.co", null, clsCotizacionMuestra.Ahora).Valor;
        var costeada = clsCotizacionMuestra.Costeada(folio: "PCN-V3", usuarioId: comercial.Id);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-V3", comercial.Id, Arg.Any<CancellationToken>()).Returns(costeada);
        _usuarios.ObtenerPorIdAsync(comercial.Id, Arg.Any<CancellationToken>()).Returns(comercial);
        _opciones.Proteccion = new clsOpcionesProteccionCorreo { Modo = "redirigir", BuzonPruebas = "qa@pruebas.local" };

        var vista = (await Servicio().PrevisualizarAsync(comercial.Id, "PCN-V3", null, Ct)).Valor;

        vista.EntornoPruebas.Should().BeTrue();
        vista.CopiaOculta.Should().BeNull("al redirigir no sale ninguna copia oculta");
        vista.Aviso.Should().Contain("sin CC ni CCO");
    }

    [Fact]
    public async Task LaVistaPreviaTraeElResumenConElTotalYLaCopiaOcultaAlComercial()
    {
        var comercial = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas", "nvargas@boterosoto.com.co", null, clsCotizacionMuestra.Ahora).Valor;
        var costeada = clsCotizacionMuestra.Costeada(folio: "PCN-V2", usuarioId: comercial.Id);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-V2", comercial.Id, Arg.Any<CancellationToken>()).Returns(costeada);
        _usuarios.ObtenerPorIdAsync(comercial.Id, Arg.Any<CancellationToken>()).Returns(comercial);

        var vista = (await Servicio().PrevisualizarAsync(comercial.Id, "PCN-V2", null, Ct)).Valor;

        vista.Resumen.Should().NotBeNull();
        vista.Resumen!.Total.Should().Be(costeada.PrecioFinal!.Value);
        vista.Resumen.Rutas.Should().ContainSingle().Which.Precio.Should().Be(costeada.Rutas[0].PrecioFinalLinea);
        vista.Resumen.Moneda.Should().Be("COP");
        vista.CuerpoHtml.Should().Contain(vista.Resumen.TotalTexto);
        vista.CopiaOculta.Should().Be("nvargas@boterosoto.com.co");
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
    public async Task LaVistaPreviaLlevaLaMismaFichaQueElEnvio()
    {
        var vista = (await Servicio().PrevisualizarAsync(_usuario, "PCN-V1", null, Ct)).Valor;

        vista.CuerpoHtml.Should().Contain(">PCN-V1<");
    }

    [Fact]
    public void LaPlantillaHtmlEscapaElTextoYConvierteLosSaltosDeLinea() =>
        clsPlantillaCorreo.Html("<script>x</script>\r\nhola")
            .Should().Contain("&lt;script&gt;x&lt;/script&gt;<br>hola")
            .And.StartWith("<div style=\"font-family:Arial,Helvetica,sans-serif;color:#1A2A3A");
}
