using Cotifly.Dominio.Seguros;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Seguros;

/// <summary>
/// El evaluador de la política de seguros con la carga aprobada. Tramo por ruta con
/// el valor total, <c>valorDesde</c> exclusivo y <c>valorHasta</c> incluido; cada medida (recomendada u obligatoria) trae su estado
/// (<c>cumplida</c> | <c>pendiente</c> | <c>sin_verificar</c>); las obligatorias se cumplen con cualquiera de sus conexos; una medida sin
/// conexos queda <c>sin_verificar</c> y nunca bloquea; valor 0 o vacío da el aviso <c>SIN_VALOR_MERCANCIA</c>.
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
        var evaluacion = _politica.Evaluar(valor, incluyeSeguro: false, []);

        evaluacion.Tramo!.Nombre.Should().Be(tramo);
        evaluacion.Tramo.Id.Should().NotBeEmpty("el bloque del contrato del 2026-11-10 lleva el id del tramo");
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
        var evaluacion = _politica.Evaluar(300 * M, incluyeSeguro: false, []);

        evaluacion.Recomendadas.Select(m => m.Codigo).Should().BeEquivalentTo(["esquema_basico", "gps", "candado_satelital", "incluir_seguro"]);
        evaluacion.Recomendadas.Should().Contain(m => m.Nombre == "Esquema básico de seguridad", "se muestra el nombre de la medida");
        evaluacion.Obligatorias.Should().BeEmpty();
        evaluacion.Pendientes.Should().BeEmpty();
    }

    [Fact]
    public void UnaMedidaSinConexosQuedaSinVerificar() =>
        _politica.Evaluar(300 * M, incluyeSeguro: false, []).Recomendadas.Single(m => m.Codigo == "gps").Estado
            .Should().Be("sin_verificar", "una medida sin conexos no se puede verificar y nunca bloquea");

    [Theory]
    [InlineData(true, "cumplida")]
    [InlineData(false, "pendiente")]
    public void IncluirSeguroSeEvaluaPorSuCodigoConLaBanderaDeLaRuta(bool incluyeSeguro, string estado) =>
        _politica.Evaluar(300 * M, incluyeSeguro, []).Recomendadas.Single(m => m.Codigo == "incluir_seguro").Estado.Should().Be(estado);

    [Fact]
    public void LaMedidaObligatoriaTraeSiEsCobrable() =>
        _politica.Evaluar(400 * M, incluyeSeguro: false, []).Obligatorias.Single(m => m.Codigo == "acompanante_vehicular").EsCobrable
            .Should().BeTrue("el acompañante se cumple agregando un conexo cobrable");

    [Fact]
    public void De350A500SinAcompananteLaObligacionQuedaPendienteConLosCincoCodigos()
    {
        var evaluacion = _politica.Evaluar(400 * M, incluyeSeguro: false, ["CARGUE_PUERTO", null]);

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
        _politica.Evaluar(400 * M, incluyeSeguro: false, [codigo]).Obligatorias.Single().Estado.Should().Be("cumplida");
        _politica.Evaluar(600 * M, incluyeSeguro: false, [codigo.ToLowerInvariant()]).Obligatorias.Single().Cumplida
            .Should().BeTrue("el código se compara sin mayúsculas");
    }

    [Fact]
    public void De350A500UnAcompananteTotalCumpleLaObligacion() =>
        _politica.Evaluar(400 * M, incluyeSeguro: false, ["AVT_AUTO"]).Obligatorias.Single().Estado
            .Should().Be("cumplida", "decisión 11: el total cumple la de parcial");

    [Theory]
    [MemberData(nameof(CadaAcompananteTotal))]
    public void DesdeSetecientosSoloLosTotalesCumplen(string codigo) =>
        _politica.Evaluar(800 * M, incluyeSeguro: false, [codigo]).Obligatorias.Single()
            .Should().Match<clsMedidaEvaluada>(m => m.Codigo == "acompanante_total" && m.Cumplida);

    [Theory]
    [InlineData("AVP_MOTO")]
    [InlineData("AVP_MOTO_ARMADA")]
    public void DesdeSetecientosUnParcialNoCumple(string codigo) =>
        _politica.Evaluar(800 * M, incluyeSeguro: false, [codigo]).Pendientes.Should().ContainSingle().Which.Codigo.Should().Be("acompanante_total");

    [Fact]
    public void UnaMedidaObligatoriaSinConexosQuedaSinVerificarYNuncaPendiente()
    {
        var politica = new clsPoliticaSeguros(
        [
            new(Guid.NewGuid(), "> 0", 0m, null,
            [
                new("gps", "GPS", EsCobrable: false, clsNivelMedidaSeguro.Obligatoria, []),
                new("candado_satelital", "Candado satelital", EsCobrable: false, clsNivelMedidaSeguro.Obligatoria, []),
            ]),
        ]);

        var evaluacion = politica.Evaluar(900 * M, incluyeSeguro: false, []);

        evaluacion.Obligatorias.Select(m => m.Codigo).Should().Equal("gps", "candado_satelital");
        evaluacion.Obligatorias.Should().OnlyContain(m => m.Estado == "sin_verificar");
        evaluacion.Pendientes.Should().BeEmpty("una medida sin conexos nunca queda obligatoria pendiente");
    }

    [Fact]
    public void ElTramoMasAltoNoExigeNadaEnCotifly() =>
        _politica.Evaluar(2_000 * M, incluyeSeguro: false, []).Should()
            .Match<clsEvaluacionPoliticaSeguro>(e => e.Recomendadas.Count == 0 && e.Obligatorias.Count == 0 && e.Aviso == null);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void SinValorDeMercanciaOConValorCeroNoHayTramoYSaleElAviso(int? valor)
    {
        var evaluacion = _politica.Evaluar(valor, incluyeSeguro: false, ["AVT_AUTO"]);

        evaluacion.Tramo.Should().BeNull();
        evaluacion.Aviso.Should().Be("SIN_VALOR_MERCANCIA");
        evaluacion.Obligatorias.Should().BeEmpty("sin tramo no se exige nada: el aviso no bloquea (decisión 4)");
        evaluacion.Recomendadas.Should().BeEmpty();
    }

    [Fact]
    public void ConLaPoliticaVaciaElValorNoCaeEnNingunTramoYNoHayAvisoDeContrato()
    {
        var evaluacion = clsPoliticaSeguros.Vacia.Evaluar(400 * M, incluyeSeguro: false, []);

        evaluacion.Tramo.Should().BeNull();
        evaluacion.Aviso.Should().BeNull("un hueco de datos no es SIN_VALOR_MERCANCIA; el front solo conoce ese aviso o null");
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
