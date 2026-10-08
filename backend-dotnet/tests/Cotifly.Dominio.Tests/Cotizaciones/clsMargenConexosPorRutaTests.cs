using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-021.8 (#135, ADR-019, decisiones 3, 4 y 6): la ruta indicada por la petición, el margen POR RUTA (validado contra el sugerido de
/// esa ruta, recalculando solo esa ruta y la cabecera), los conexos por ruta (quitar = borrar la fila, cambiar la cantidad) y el levante
/// copiado a cada camión. Pliego de 3 rutas con margen de 15 %: fletes 850.000, 1.700.000 y 425.000 → precios 1.000.000, 2.000.000 y
/// 500.000 (total 3.500.000).
/// </summary>
public sealed class clsMargenConexosPorRutaTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    private static clsCotizacionSolicitud TresRutas() => clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m]);

    // ------------------------------------------------------------------ Ruta indicada

    [Fact]
    public void ConUnaSolaRutaSinIndicarlaSeTomaEsaRuta()
    {
        var cotizacion = clsCotizacionMuestra.Costeada();

        var ruta = cotizacion.ResolverRuta(null, null);

        ruta.Valor.Should().BeSameAs(cotizacion.PrimeraRuta);
    }

    [Fact]
    public void ConVariasRutasSinIndicarlaSePideLaRutaConLaListaSinMontos()
    {
        var cotizacion = TresRutas();

        var error = cotizacion.ResolverRuta(null, null).Error;

        error.Codigo.Should().Be(clsCodigosError.RutaRequerida);
        var rutas = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)error.Datos!["rutas"]!;
        rutas.Select(r => r["linea_orden"]).Should().Equal(1, 2, 3);
        rutas.Select(r => r["destino"]).Should().Equal("Destino 1", "Destino 2", "Destino 3");
        rutas.Should().OnlyContain(r => r.Keys.All(k => k == "linea_orden" || k == "linea_solicitud_id" || k == "origen" || k == "destino"),
            "la lista de rutas no lleva montos");
    }

    [Fact]
    public void LaRutaSeIndicaPorOrdenOPorIdYDebenCoincidir()
    {
        var cotizacion = TresRutas();
        var segunda = cotizacion.RutasOrdenadas[1];

        cotizacion.ResolverRuta(2, null).Valor.Should().BeSameAs(segunda);
        cotizacion.ResolverRuta(null, segunda.Id).Valor.Should().BeSameAs(segunda);
        cotizacion.ResolverRuta(2, segunda.Id).Valor.Should().BeSameAs(segunda);
        cotizacion.ResolverRuta(3, segunda.Id).Error.Codigo.Should().Be(clsCodigosError.RutaInexistente, "orden e id de rutas distintas");
        cotizacion.ResolverRuta(4, null).Error.Codigo.Should().Be(clsCodigosError.RutaInexistente);
        cotizacion.ResolverRuta(null, Guid.NewGuid()).Error.Codigo.Should().Be(clsCodigosError.RutaInexistente);
    }

    // ------------------------------------------------------------------ Margen por ruta (decisión 6)

    [Fact]
    public void ElMargenDeLaRutaDosSoloRecalculaEsaRutaYLaCabecera()
    {
        var cotizacion = TresRutas();
        cotizacion.PrecioFinal.Should().Be(3_500_000m);

        cotizacion.AplicarMargenNegociado(2, 0.20m, _ahora).Valor.Should().BeTrue();

        cotizacion.RutasOrdenadas.Select(r => r.MargenAplicado).Should().Equal(0.15m, 0.20m, 0.15m);
        cotizacion.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(1_000_000m, 2_125_000m, 500_000m);
        cotizacion.PrecioFinal.Should().Be(3_625_000m);
    }

    [Fact]
    public void LaJustificacionSeExigeContraElSugeridoDeEsaRuta()
    {
        var cotizacion = TresRutas();
        var segunda = cotizacion.RutasOrdenadas[1];
        segunda.RegistrarMargenCosteo(0.15m, 0.25m, OrigenMargen.SapTm, "margen");
        cotizacion.RecalcularTotales(_ahora);

        cotizacion.ValidarAjusteMargen(1, 0.20m, null).Should().BeNull("20 % está por encima del 15 % de la ruta 1");
        cotizacion.ValidarAjusteMargen(2, 0.20m, null)!.Codigo.Should().Be(clsCodigosError.JustificacionRequerida, "la ruta 2 sugiere 25 % (SAP TM)");
        cotizacion.ValidarAjusteMargen(2, 0.20m, "Cliente estratégico").Should().BeNull();
        cotizacion.ValidarAjusteMargen(9, 0.20m, null)!.Codigo.Should().Be(clsCodigosError.RutaInexistente);
    }

    [Fact]
    public void SinPrecioEnLaRutaNoSeAjusta()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m]);
        cotizacion.AgregarRuta(new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", PesoKg = 1m }, 1, 3);

        cotizacion.ValidarAjusteMargen(2, 0.20m, null)!.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }

    // ------------------------------------------------------------------ Conexos por ruta (decisión 3)

    [Fact]
    public void QuitarUnConexoBorraLaFilaDeEsaRutaYRecalculaSoloEsaRutaYLaCabecera()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(TresRutas(), "CX-1", "Cargue", 85_000m, 2m, orden: 2);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-2", "Escolta", 42_500m, 1m, orden: 2);
        cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(2_250_000m, "(1.700.000 + 170.000 + 42.500) / 0,85");

        var movimientos = cotizacion.AjustarConexosRuta(2, new Dictionary<string, decimal> { ["CX-2"] = 1m }, _ahora).Valor;

        movimientos.Should().ContainSingle().Which.Should().Be(new clsMovimientoConexo("CX-1", "Cargue", 2m, null));
        movimientos[0].Quitado.Should().BeTrue();
        cotizacion.RutasOrdenadas[1].Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-2");
        cotizacion.RutasOrdenadas[1].CostoServiciosConexos.Should().Be(42_500m);
        cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(2_050_000m, "(1.700.000 + 42.500) / 0,85");
        cotizacion.PrecioFinal.Should().Be(3_550_000m);
    }

    [Fact]
    public void CambiarLaCantidadConservaElPrecioUnitarioDeNexo()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(TresRutas(), "CX-1", "Cargue", 85_000m, 2m, orden: 2);

        var movimientos = cotizacion.AjustarConexosRuta(2, new Dictionary<string, decimal> { ["cx-1"] = 1m }, _ahora).Valor;

        movimientos.Should().ContainSingle().Which.Should().Be(new clsMovimientoConexo("CX-1", "Cargue", 2m, 1m));
        var conexo = cotizacion.RutasOrdenadas[1].Conexos.Single();
        conexo.Precio.Should().Be(85_000m);
        conexo.Cantidad.Should().Be(1m);
        cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(2_100_000m, "(1.700.000 + 85.000) / 0,85");
    }

    [Fact]
    public void LasMismasCantidadesNoCambianNada()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(TresRutas(), "CX-1", "Cargue", 85_000m, 2m, orden: 2);

        cotizacion.AjustarConexosRuta(2, new Dictionary<string, decimal> { ["CX-1"] = 2m }, _ahora).Valor.Should().BeEmpty();
    }

    [Fact]
    public void UnConexoQueNoEsDeLaRutaEsUnErrorDeProgramacion()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(TresRutas(), "CX-1", "Cargue", 85_000m, 2m, orden: 2);

        var accion = () => cotizacion.AjustarConexosRuta(1, new Dictionary<string, decimal> { ["CX-1"] = 1m }, _ahora);

        accion.Should().Throw<ArgumentException>("el caso de uso valida antes que el conexo sea de la ruta");
    }

    // ------------------------------------------------------------------ Levante (decisión 4)

    [Fact]
    public void CambiarElLevanteDeUnaRutaLoCopiaACadaCamionYRecalcula()
    {
        var cotizacion = TresRutas();

        cotizacion.CambiarValorLevante(1, 85_000m, _ahora).Valor.Should().BeTrue();

        var ruta = cotizacion.RutasOrdenadas[0];
        ruta.ValorLevanteContenedor.Should().Be(85_000m);
        ruta.Vehiculos.Should().OnlyContain(v => v.CostoLevanteContenedor == 85_000m);
        ruta.CostoLevanteContenedorTotal.Should().Be(85_000m);
        ruta.PrecioFinalLinea.Should().Be(1_100_000m, "(850.000 + 85.000) / 0,85");
        cotizacion.PrecioFinal.Should().Be(3_600_000m);
        cotizacion.CambiarValorLevante(1, 85_000m, _ahora).Valor.Should().BeFalse("el mismo valor no cambia nada");
    }

    [Fact]
    public void ElLevanteNoCambiaDespuesDeEntregarLaCotizacion()
    {
        var cotizacion = TresRutas();
        cotizacion.RegistrarEnvio();

        cotizacion.CambiarValorLevante(1, 85_000m, _ahora).Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        cotizacion.CambiarValorLevante(1, -1m, _ahora).Error.Codigo.Should().Be(clsCotizacionSolicitud.CodigoError);
        cotizacion.RutasOrdenadas[0].ValorLevanteContenedor.Should().BeNull();
    }
}
