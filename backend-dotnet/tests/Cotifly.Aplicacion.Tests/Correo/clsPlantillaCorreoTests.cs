using System.Net;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Pdf.Services;

namespace Cotifly.Aplicacion.Tests.Correo;

/// <summary>#262: diseño del HTML del correo (cotización formal): resumen con su ficha, tabla de rutas con su tope, nota del PDF y firma.</summary>
public sealed class clsPlantillaCorreoTests
{
    private static clsResumenCorreo Resumen(int rutas) => new(
        "COP",
        rutas * 1_000_000m,
        "$" + (rutas * 1_000_000m).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.'),
        [.. Enumerable.Range(1, rutas).Select(i => new clsRutaResumenCorreo(i, $"Origen {i} → Destino {i}", 1_000_000m, "$1.000.000"))],
        clsTextosEstandar.LeyendaImpuestos,
        null);

    [Fact]
    public void SinDatosNoHayFicha() =>
        clsPlantillaCorreo.Html("Hola", Resumen(2), firmaComercial: "Ana · ana@boterosoto.com.co").Should()
            .NotContain(">Folio<").And.NotContain(">Fecha<").And.NotContain(">Cliente<");

    [Fact]
    public void ConDatosLaFichaLlevaElFolioLaFechaYElClienteEnEseOrden()
    {
        var datos = new clsDatosCorreo("PCN-SC-03112026-001", "Ferretería <El Martillo>", new DateOnly(2026, 11, 3));

        var html = clsPlantillaCorreo.Html("Hola", Resumen(1), datos: datos);

        html.Should().Contain(">PCN-SC-03112026-001<")
            .And.Contain(">3 de noviembre de 2026<")
            .And.Contain(">Ferreter&#237;a &lt;El Martillo&gt;<", "el nombre del cliente llega escapado");
        html.IndexOf(">Folio<", StringComparison.Ordinal).Should()
            .BeLessThan(html.IndexOf(">Fecha<", StringComparison.Ordinal))
            .And.BeLessThan(html.IndexOf(">Cliente<", StringComparison.Ordinal));
    }

    [Fact]
    public void LaFichaSoloLlevaLoQueSeConoce() =>
        clsPlantillaCorreo.Html("Hola", Resumen(1), datos: new clsDatosCorreo(Fecha: new DateOnly(2026, 1, 15))).Should()
            .Contain(">15 de enero de 2026<")
            .And.NotContain(">Folio<").And.NotContain(">Cliente<");

    [Fact]
    public void ElResumenListaHastaDiezRutasYElTotalEsElDeTodas()
    {
        var html = clsPlantillaCorreo.Html("Hola", Resumen(12));

        html.Should().Contain($">{WebUtility.HtmlEncode("Origen 10 → Destino 10")}<")
            .And.NotContain(WebUtility.HtmlEncode("Origen 11"))
            .And.Contain(WebUtility.HtmlEncode(clsPlantillaCorreo.RutasRestantes(2)))
            .And.Contain("$12.000.000 COP");
    }

    [Fact]
    public void ConDiezRutasNoHayLineaDeRutasRestantes() =>
        clsPlantillaCorreo.Html("Hola", Resumen(10)).Should().NotContain(WebUtility.HtmlEncode("rutas más"));

    [Fact]
    public void LaLineaDeRutasRestantesConcuerdaEnSingularYPlural()
    {
        clsPlantillaCorreo.RutasRestantes(1).Should().Be("… y 1 ruta más: el detalle completo está en el PDF adjunto.");
        clsPlantillaCorreo.RutasRestantes(290).Should().Be("… y 290 rutas más: el detalle completo está en el PDF adjunto.");
    }

    [Fact]
    public void LlevaElAnchoMaximoElEnvoltorioDeOutlookElPdfYElPieLegal() =>
        clsPlantillaCorreo.Html("Hola").Should()
            .Contain($"max-width:{clsPlantillaCorreo.AnchoMaximo}px")
            .And.Contain($"<!--[if mso]><table role=\"presentation\" width=\"{clsPlantillaCorreo.AnchoMaximo}\"")
            .And.Contain(WebUtility.HtmlEncode(clsPlantillaCorreo.TextoAdjunto))
            .And.Contain(WebUtility.HtmlEncode(clsTextosEstandar.PieEmpresa))
            .And.Contain(clsTextosEstandar.Web);

    [Fact]
    public void ElOrdenEsElDeUnaCartaConLaDespedidaAlFinalAntesDeLaFirma()
    {
        var html = clsPlantillaCorreo.Html(
            "Estimado cliente,", Resumen(1), "[QA] Ambiente de pruebas", "Ana · ana@boterosoto.com.co", new clsDatosCorreo(Folio: "PCN-1"));

        string[] enOrden =
        [
            "[QA] Ambiente de pruebas", "Estimado cliente,", WebUtility.HtmlEncode(clsPlantillaCorreo.TituloResumen), ">Folio<",
            WebUtility.HtmlEncode(clsPlantillaCorreo.TituloRutas), "$1.000.000 COP", WebUtility.HtmlEncode(clsPlantillaCorreo.TextoAdjunto),
            clsTextosEstandar.Despedida, ">Ana<", ">ana@boterosoto.com.co<", WebUtility.HtmlEncode(clsTextosEstandar.PieEmpresa),
        ];
        var posiciones = enOrden.Select(t => html.IndexOf(t, StringComparison.Ordinal)).ToList();
        posiciones.Should().NotContain(-1).And.BeInAscendingOrder();
    }

    // #261 (HU-046): el texto de seguros va después de la nota del PDF adjunto y antes de la despedida, con su etiqueta.
    [Fact]
    public void ElTextoDeSegurosVaDespuesDelPdfAdjuntoYAntesDeLaDespedida()
    {
        var html = clsPlantillaCorreo.Html("Hola", Resumen(1), textoSeguros: "Ampara la responsabilidad del transporte.");

        html.Should().Contain(WebUtility.HtmlEncode(clsTextosEstandar.TituloSegurosPropuesta))
            .And.Contain("Ampara la responsabilidad del transporte.");
        var seguros = html.IndexOf("Ampara la responsabilidad del transporte.", StringComparison.Ordinal);
        seguros.Should().BeGreaterThan(html.IndexOf(WebUtility.HtmlEncode(clsPlantillaCorreo.TextoAdjunto), StringComparison.Ordinal))
            .And.BeLessThan(html.IndexOf(clsTextosEstandar.Despedida, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinTextoDeSegurosElCorreoNoLlevaElBloque(string? textoSeguros) =>
        clsPlantillaCorreo.Html("Hola", Resumen(1), textoSeguros: textoSeguros).Should()
            .NotContain(WebUtility.HtmlEncode(clsTextosEstandar.TituloSegurosPropuesta));

    [Fact]
    public void SinFirmaDelComercialLaDespedidaVaConElEquipoComercial() =>
        clsPlantillaCorreo.Html("Hola").Should()
            .Contain($">{WebUtility.HtmlEncode(clsPlantillaCorreo.FirmaGenerica)}<");

    [Fact]
    public void LaFichaDiceCuantasRutasEstanCosteadasYElCorreoNoLlevaLaLeyendaDeImpuestos()
    {
        var resumen = Resumen(3) with
        {
            Rutas = [.. Resumen(3).Rutas.Select(r => r.Orden == 2 ? r with { Precio = null, PrecioTexto = "Sin costeo" } : r)],
        };

        var html = clsPlantillaCorreo.Html("Hola", resumen);

        clsPlantillaCorreo.RutasCosteadas(resumen).Should().Be("2");
        html.Should().Contain($">{clsPlantillaCorreo.EtiquetaRutasCosteadas}<").And.Contain(">2<")
            .And.NotContain(WebUtility.HtmlEncode(clsTextosEstandar.LeyendaImpuestos));
    }

    [Fact]
    public void LaFirmaVaUnaParteDebajoDeLaOtra() =>
        clsPlantillaCorreo.Html("Hola", firmaComercial: clsPlantillaCorreo.FirmaComercial("Ana", "ana@boterosoto.com.co", "300 000 0000")).Should()
            .Contain(">Ana</div>").And.Contain(">ana@boterosoto.com.co</div>").And.Contain(">Cel. 300 000 0000</div>");

    [Fact]
    public void ElEncabezadoLlevaCotizacionConElFolioDebajoYElLogo()
    {
        var html = clsPlantillaCorreo.Html(
            "Hola", datos: new clsDatosCorreo(Folio: "PCN-SC-03112026-001"), logoUrl: "https://www.boterosoto.com.co/logo.png?v=1&x=2");

        html.Should().Contain($">{WebUtility.HtmlEncode(clsPlantillaCorreo.TituloCorreo)}</div><div style=\"color:#5F6B7A;margin-top:2px\">PCN-SC-03112026-001</div>")
            .And.Contain("<img src=\"https://www.boterosoto.com.co/logo.png?v=1&amp;x=2\"", "la URL va escapada en el atributo")
            .And.Contain($"alt=\"{clsPlantillaCorreo.Marca}\"");
        html.IndexOf("PCN-SC-03112026-001", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Hola", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("logo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/logo.png")]
    public void SinUnaUrlHttpDelLogoElEncabezadoLlevaLaMarcaEnTexto(string? logoUrl) =>
        clsPlantillaCorreo.Html("Hola", logoUrl: logoUrl).Should()
            .NotContain("<img").And.Contain($">{clsPlantillaCorreo.Marca}</span>");

    [Fact]
    public void ElCuerpoPorDefectoNoTraeDespedidaPorqueLaPoneElHtml() =>
        clsPlantillaCorreo.CuerpoPorDefecto.Should().StartWith("Estimado cliente,").And.NotContain(clsTextosEstandar.Despedida);
}
