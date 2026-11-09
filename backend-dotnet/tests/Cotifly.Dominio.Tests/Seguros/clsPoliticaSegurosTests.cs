using Cotifly.Dominio.Seguros;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Seguros;

/// <summary>
/// HU-046.3 (#269): el evaluador de la política de seguros con la carga aprobada (HU-046.1 #267). Tramo por ruta con el valor total,
/// <c>valorDesde</c> exclusivo y <c>valorHasta</c> incluido; las obligatorias se cumplen con cualquiera de sus conexos; una medida sin
/// conexos nunca queda obligatoria pendiente; valor 0 o vacío da el aviso.
/// </summary>
public sealed class clsPoliticaSegurosTests
{
    private const decimal M = clsPoliticaSegurosMuestra.Millon;
    private static readonly clsPoliticaSeguros _politica = clsPoliticaSegurosMuestra.Aprobada();

    public static TheoryData<decimal, string> UnCasoPorTramo() => new()
    {
        { 100 * M, "≤ 150 M" },
        { 200 * M, "> 150 – 250 M" },
        { 300 * M, "> 250 – 350 M" },
        { 400 * M, "> 350 – 500 M" },
        { 600 * M, "> 500 – 700 M" },
        { 800 * M, "> 700 – 1.000 M" },
        { 5_000 * M, "> 1.000 M" },
    };

    [Theory]
    [MemberData(nameof(UnCasoPorTramo))]
    public void CadaValorCaeEnSuTramo(decimal valor, string tramo)
    {
        var evaluacion = _politica.Evaluar(valor, []);

        evaluacion.Tramo!.Nombre.Should().Be(tramo);
        evaluacion.Aviso.Should().BeNull();
    }

    [Theory]
    [InlineData(0.01, "≤ 150 M")]
    [InlineData(150_000_000, "≤ 150 M")]
    [InlineData(150_000_000.01, "> 150 – 250 M")]
    [InlineData(250_000_000, "> 150 – 250 M")]
    [InlineData(250_000_000.01, "> 250 – 350 M")]
    [InlineData(350_000_000, "> 250 – 350 M")]
    [InlineData(350_000_000.01, "> 350 – 500 M")]
    [InlineData(500_000_000, "> 350 – 500 M")]
    [InlineData(700_000_000, "> 500 – 700 M")]
    [InlineData(1_000_000_000, "> 700 – 1.000 M")]
    [InlineData(1_000_000_000.01, "> 1.000 M")]
    public void EnElLimiteElValorCaeEnElTramoDeAbajo(double valor, string tramo) =>
        _politica.TramoDe((decimal)valor)!.Nombre.Should().Be(tramo, "valorDesde es exclusivo y valorHasta incluido (decisión 3)");

    [Fact]
    public void ElTramoDeAbajoSoloTieneRecomendacionesYNingunaObligatoria()
    {
        var evaluacion = _politica.Evaluar(300 * M, []);

        evaluacion.Recomendadas.Select(m => m.Codigo).Should().Equal("esquema_basico", "gps", "candado_satelital", "incluir_seguro");
        evaluacion.Recomendadas[0].Nombre.Should().Be("Esquema básico de seguridad", "se muestra el nombre de la medida");
        evaluacion.Obligatorias.Should().BeEmpty();
        evaluacion.Pendientes.Should().BeEmpty();
    }

    [Fact]
    public void De350A500SinAcompananteLaObligacionQuedaPendienteConLosCincoCodigos()
    {
        var evaluacion = _politica.Evaluar(400 * M, ["CARGUE_PUERTO", null]);

        var medida = evaluacion.Obligatorias.Should().ContainSingle().Subject;
        medida.Codigo.Should().Be("acompanante_vehicular");
        medida.Estado.Should().Be("pendiente");
        medida.Conexos.Should().Equal(clsPoliticaSegurosMuestra.Acompanantes);
        evaluacion.Pendientes.Should().ContainSingle();
        evaluacion.Recomendadas.Select(m => m.Codigo).Should().NotContain("acompanante_vehicular");
    }

    public static TheoryData<string> CadaAcompanante() => [.. clsPoliticaSegurosMuestra.Acompanantes];

    public static TheoryData<string> CadaAcompananteTotal() => [.. clsPoliticaSegurosMuestra.AcompanantesTotales];

    [Theory]
    [MemberData(nameof(CadaAcompanante))]
    public void CualquieraDeLosConexosDeLaMedidaCumpleLaObligacionDe350A700(string codigo)
    {
        _politica.Evaluar(400 * M, [codigo]).Obligatorias.Single().Estado.Should().Be("cumplida");
        _politica.Evaluar(600 * M, [codigo.ToLowerInvariant()]).Obligatorias.Single().Cumplida.Should().BeTrue("el código se compara sin mayúsculas");
    }

    [Fact]
    public void De350A500UnAcompananteTotalCumpleLaObligacion() =>
        _politica.Evaluar(400 * M, ["AVT_AUTO"]).Obligatorias.Single().Estado.Should().Be("cumplida", "decisión 11: el total cumple la de parcial");

    [Theory]
    [MemberData(nameof(CadaAcompananteTotal))]
    public void DesdeSetecientosSoloLosTotalesCumplen(string codigo) =>
        _politica.Evaluar(800 * M, [codigo]).Obligatorias.Single().Should().Match<clsMedidaObligatoria>(m => m.Codigo == "acompanante_total" && m.Cumplida);

    [Theory]
    [InlineData("AVP_MOTO")]
    [InlineData("AVP_MOTO_ARMADA")]
    public void DesdeSetecientosUnParcialNoCumple(string codigo) =>
        _politica.Evaluar(800 * M, [codigo]).Pendientes.Should().ContainSingle().Which.Codigo.Should().Be("acompanante_total");

    [Fact]
    public void UnaMedidaObligatoriaSinConexosNuncaQuedaPendienteYSeMuestraComoRecomendada()
    {
        var politica = new clsPoliticaSeguros(
        [
            new(Guid.NewGuid(), "> 0", 0m, null,
            [
                new("gps", "GPS", clsNivelMedidaSeguro.Obligatoria, []),
                new("candado_satelital", "Candado satelital", clsNivelMedidaSeguro.Obligatoria, []),
            ]),
        ]);

        var evaluacion = politica.Evaluar(900 * M, []);

        evaluacion.Obligatorias.Should().BeEmpty();
        evaluacion.Pendientes.Should().BeEmpty();
        evaluacion.Recomendadas.Select(m => m.Codigo).Should().Equal("gps", "candado_satelital");
    }

    [Fact]
    public void ElTramoMasAltoNoExigeNadaEnCotifly() =>
        _politica.Evaluar(2_000 * M, []).Should().Match<clsEvaluacionPoliticaSeguro>(e => e.Recomendadas.Count == 0 && e.Obligatorias.Count == 0 && e.Aviso == null);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void SinValorDeMercanciaOConValorCeroNoHayTramoYSaleElAviso(int? valor)
    {
        var evaluacion = _politica.Evaluar(valor, ["AVT_AUTO"]);

        evaluacion.Tramo.Should().BeNull();
        evaluacion.Aviso.Should().Be(clsPoliticaSeguros.AvisoSinValorMercancia);
        evaluacion.Obligatorias.Should().BeEmpty("sin tramo no se exige nada: el aviso no bloquea (decisión 4)");
        evaluacion.Recomendadas.Should().BeEmpty();
    }

    [Fact]
    public void ConLaPoliticaVaciaElValorNoCaeEnNingunTramoYSaleOtroAviso()
    {
        var evaluacion = clsPoliticaSeguros.Vacia.Evaluar(400 * M, []);

        evaluacion.Tramo.Should().BeNull();
        evaluacion.Aviso.Should().Be(clsPoliticaSeguros.AvisoSinTramo);
        evaluacion.Pendientes.Should().BeEmpty();
    }

    [Fact]
    public void LosTramosQuedanOrdenadosAunqueLleguenDesordenados()
    {
        var politica = new clsPoliticaSeguros([.. _politica.Tramos.Reverse()]);

        politica.Tramos.Select(t => t.ValorDesde).Should().BeInAscendingOrder();
        politica.TramoDe(400 * M)!.Nombre.Should().Be("> 350 – 500 M");
    }
}
