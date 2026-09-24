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
        var mapeo = clsSapMapeoTrqType.Crear(" optn ", "Nacional").Valor;

        mapeo.TrqType.Should().Be("OPTN");
        mapeo.SalesOrg.Should().BeEmpty();
        mapeo.EsComodin.Should().BeTrue();
        mapeo.Activo.Should().BeTrue();
    }

    [Fact]
    public void ElMapeoConOrganizacionEspecificaNoEsComodin() =>
        clsSapMapeoTrqType.Crear("OPTN", "Nacional", "50000005").Valor.EsComodin.Should().BeFalse();

    [Theory]
    [InlineData("TRQTYPE9X", "Nacional", "")]
    [InlineData("OPTN", "", "")]
    [InlineData("OPTN", "Nacional", "1234567890123")]
    public void ElMapeoRespetaLosLargosDelEsquema(string trqType, string linea, string salesOrg) =>
        clsSapMapeoTrqType.Crear(trqType, linea, salesOrg).EsFallo.Should().BeTrue();

    [Fact]
    public void UnAjusteCalculaLaDiferenciaConCuatroDecimales()
    {
        var ajuste = clsMargenAjuste.Registrar(0.15m, 0.123456m, "Cliente estratégico").Valor;

        ajuste.Diferencia.Should().Be(-0.0265m);
        ajuste.Motivo.Should().Be("Cliente estratégico");
    }

    [Fact]
    public void UnAjusteExigeMotivo() =>
        clsMargenAjuste.Registrar(0.15m, 0.1m, " ").Error.Codigo.Should().Be(clsMargenAjuste.CodigoError);

    [Theory]
    [InlineData(1.0, 0.1)]
    [InlineData(0.1, -0.1)]
    public void UnAjusteExigeMargenesEnCeroUno(double politica, double aplicado) =>
        clsMargenAjuste.Registrar((decimal)politica, (decimal)aplicado, "motivo").EsFallo.Should().BeTrue();
}
