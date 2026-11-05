using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Tests.Margen;

public sealed class clsMargenTests
{
    [Fact]
    public void UnaPoliticaNuevaQuedaActivaConMetodoMargen()
    {
        var politica = clsMargenPolitica.Crear(" Nacional ", 0.15m).Valor;

        politica.LineaNegocio.Should().Be("Nacional");
        politica.Margen.Should().Be(0.15m);
        politica.MetodoCalculo.Should().Be("margen");
        politica.Activa.Should().BeTrue();
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1)]
    public void ElMargenDePoliticaDebeEstarEnCeroUno(double margen) =>
        clsMargenPolitica.Crear("Nacional", (decimal)margen).EsFallo.Should().BeTrue();

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(0.3)]
    public void ConMetodoMarkupElMargenPuedeSerMayorOIgualAUno(double margen) =>
        clsMargenPolitica.Crear("Nacional", (decimal)margen, clsReglaMargen.MetodoMarkup).EsExitoso.Should().BeTrue();

    [Fact]
    public void ConMetodoMarkupElMargenTampocoPuedeSerNegativo() =>
        clsMargenPolitica.Crear("Nacional", -0.1m, clsReglaMargen.MetodoMarkup).EsFallo.Should().BeTrue();

    [Fact]
    public void CambiarMargenRespetaElMetodoDeLaLinea()
    {
        var markup = clsMargenPolitica.Crear("Nacional", 0.2m, "markup").Valor;
        var margen = clsMargenPolitica.Crear("Distribucion", 0.2m).Valor;

        markup.CambiarMargen(1.25m).EsExitoso.Should().BeTrue();
        margen.CambiarMargen(1.25m).EsFallo.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" MARGEN ", true)]
    [InlineData("markup", false)]
    public void UnMetodoNuloOVacioSeTrataComoMargen(string? metodo, bool esMargen) =>
        clsReglaMargen.EsMetodoMargen(metodo).Should().Be(esMargen);

    [Fact]
    public void LaLineaNoSuperaCuarentaCaracteres() =>
        clsMargenPolitica.Crear(new string('L', 41), 0.1m).EsFallo.Should().BeTrue();

    [Fact]
    public void CambiarMargenValidaElRangoYNoModificaSiFalla()
    {
        var politica = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;

        politica.CambiarMargen(1.2m).EsFallo.Should().BeTrue();
        politica.Margen.Should().Be(0.15m);
        politica.CambiarMargen(0.2m).EsExitoso.Should().BeTrue();
        politica.Margen.Should().Be(0.2m);
    }

    [Fact]
    public void UnaPoliticaSePuedeDesactivarYReactivar()
    {
        var politica = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;

        politica.Desactivar();
        politica.Activa.Should().BeFalse();
        politica.Activar();
        politica.Activa.Should().BeTrue();
    }

    [Fact]
    public void ElMapeoNormalizaElTrqTypeYUsaElComodinPorDefecto()
    {
        var mapeo = clsSapMapeoTrqType.Crear(" optn ", 1, "Nacional").Valor;

        mapeo.TrqType.Should().Be("OPTN");
        mapeo.LineaNegocioId.Should().Be(1);
        mapeo.SalesOrg.Should().BeEmpty();
        mapeo.EsComodin.Should().BeTrue();
        mapeo.Activo.Should().BeTrue();
    }

    [Fact]
    public void ElMapeoConOrganizacionEspecificaNoEsComodin() =>
        clsSapMapeoTrqType.Crear("OPTN", 1, "Nacional", "50000005").Valor.EsComodin.Should().BeFalse();

    [Theory]
    [InlineData("TRQTYPE9X", 1, "Nacional", "")]
    [InlineData("OPTN", 1, "", "")]
    [InlineData("OPTN", 1, "Nacional", "1234567890123")]
    [InlineData("OPTN", 0, "Nacional", "")]
    [InlineData("OPTN", -3, "Nacional", "")]
    public void ElMapeoRespetaLosLargosDelEsquemaYExigeElIdDeLaLinea(string trqType, int lineaNegocioId, string linea, string salesOrg) =>
        clsSapMapeoTrqType.Crear(trqType, lineaNegocioId, linea, salesOrg).EsFallo.Should().BeTrue();

    [Fact]
    public void UnAjusteCalculaLaDiferenciaConCuatroDecimales()
    {
        var ajuste = clsMargenAjuste.Registrar(0.15m, 0.123456m, "Cliente estratégico").Valor;

        ajuste.Diferencia.Should().Be(-0.0265m);
        ajuste.Motivo.Should().Be("Cliente estratégico");
    }

    // Los márgenes se guardan con la escala de la columna (4 decimales) y la diferencia sale de esos valores.
    [Fact]
    public void UnAjusteRedondeaLosMargenesACuatroDecimalesAntesDeLaDiferencia()
    {
        var ajuste = clsMargenAjuste.Registrar(0.10005m, 0.20015m, "motivo").Valor;

        ajuste.MargenPolitica.Should().Be(0.1m);     // 0.10005 → 0.1000 (half-to-even)
        ajuste.MargenAplicado.Should().Be(0.2002m);  // 0.20015 → 0.2002 (half-to-even)
        ajuste.Diferencia.Should().Be(0.1002m);      // 0.2002 − 0.1000; redondear la diferencia cruda daría 0.1001
        ajuste.Diferencia.Should().Be(ajuste.MargenAplicado - ajuste.MargenPolitica);
    }

    [Fact]
    public void UnMargenQueRedondeaAUnoSeRechazaConMetodoMargen() =>
        clsMargenAjuste.Registrar(0.15m, 0.99995m, "motivo").Error.Codigo.Should().Be("AJUSTE_MARGEN_INVALIDO");

    [Fact]
    public void UnAjusteExigeMotivo() =>
        clsMargenAjuste.Registrar(0.15m, 0.1m, " ").Error.Codigo.Should().Be(clsMargenAjuste.CodigoError);

    [Theory]
    [InlineData(1.0, 0.1)]
    [InlineData(0.1, -0.1)]
    [InlineData(0.1, 1.0)]
    public void ConMetodoMargenUnAjusteExigeMargenesEnCeroUno(double politica, double aplicado) =>
        clsMargenAjuste.Registrar((decimal)politica, (decimal)aplicado, "motivo").Error.Codigo.Should().Be(clsMargenAjuste.CodigoError);

    // Deuda de ADR-008 resuelta en #10: el ajuste valida por método; con markup admite m ≥ 1.
    [Fact]
    public void ConMetodoMarkupUnAjustePuedeSerMayorOIgualAUno()
    {
        var ajuste = clsMargenAjuste.Registrar(0.8m, 1.25m, "Recargo pactado", metodoCalculo: clsReglaMargen.MetodoMarkup).Valor;

        ajuste.MargenAplicado.Should().Be(1.25m);
        ajuste.Diferencia.Should().Be(0.45m);
    }

    [Fact]
    public void ConMetodoMarkupUnAjusteTampocoPuedeSerNegativo() =>
        clsMargenAjuste.Registrar(0.2m, -0.01m, "motivo", metodoCalculo: clsReglaMargen.MetodoMarkup).EsFallo.Should().BeTrue();

    [Fact]
    public void UnAjusteGuardaComercialCotizacionYFolioNormalizado()
    {
        var usuario = Guid.NewGuid();
        var cotizacion = Guid.NewGuid();

        var ajuste = clsMargenAjuste.Registrar(0.15m, 0.12m, " Cliente estratégico ", usuario, cotizacion, "  ").Valor;

        ajuste.UsuarioId.Should().Be(usuario);
        ajuste.CotizacionId.Should().Be(cotizacion);
        ajuste.Folio.Should().BeNull();
        ajuste.Motivo.Should().Be("Cliente estratégico");
        ajuste.Diferencia.Should().Be(-0.03m);
    }

    [Fact]
    public void LaAdministracionCambiaMargenMetodoYEstadoALaVez()
    {
        var politica = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;

        var resultado = politica.Actualizar(0.2m, " MARKUP ", activa: false);

        resultado.EsExitoso.Should().BeTrue();
        politica.Margen.Should().Be(0.2m);
        politica.MetodoCalculo.Should().Be("markup");
        politica.Activa.Should().BeFalse();
    }

    [Theory]
    [InlineData(0.2, "otro")]
    [InlineData(0.2, "")]
    [InlineData(-0.1, "margen")]
    [InlineData(1.0, "margen")]
    public void UnaEdicionInvalidaNoCambiaLaLinea(double margen, string metodo)
    {
        var politica = clsMargenPolitica.Crear("Nacional", 0.15m).Valor;

        var resultado = politica.Actualizar((decimal)margen, metodo, activa: false);

        resultado.Error.Codigo.Should().Be(clsMargenPolitica.CodigoError);
        politica.Margen.Should().Be(0.15m);
        politica.MetodoCalculo.Should().Be("margen");
        politica.Activa.Should().BeTrue();
    }
}
