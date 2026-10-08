using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Agregado de HU-021 (#130, ADR-019): cabecera → rutas → camiones, mercancías y conexos. Reglas de <c>CAMBIOS_BACKEND</c> §9: totales
/// guardados por ruta y cabecera (§9.1), levante copiado a cada camión (decisión 4) y resumen del estado "el más atrasado" (§9.2).
/// </summary>
public sealed class clsAgregadoRutasTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    // ------------------------------------------------------------------ §9.2 Resumen del estado

    [Theory]
    [InlineData(new[] { EstadoCosteo.Autocosteo, EstadoCosteo.Autocosteo }, EstadoCosteo.Autocosteo)]
    [InlineData(new[] { EstadoCosteo.Autocosteo, EstadoCosteo.PendienteCosteo }, EstadoCosteo.PendienteCosteo)]
    [InlineData(new[] { EstadoCosteo.Autocosteo, EstadoCosteo.Entregada }, EstadoCosteo.Entregada)]
    [InlineData(new[] { EstadoCosteo.Entregada, EstadoCosteo.Descartada }, EstadoCosteo.Entregada)]
    [InlineData(new[] { EstadoCosteo.Descartada, EstadoCosteo.Descartada }, EstadoCosteo.Descartada)]
    [InlineData(new[] { EstadoCosteo.CosteoVencido, EstadoCosteo.ErrorEnvio, EstadoCosteo.Autocosteo }, EstadoCosteo.ErrorEnvio)]
    [InlineData(new[] { EstadoCosteo.PendienteEnvio, EstadoCosteo.CosteoVencido }, EstadoCosteo.CosteoVencido)]
    [InlineData(new[] { EstadoCosteo.PendienteCosteo, EstadoCosteo.PendienteEnvio }, EstadoCosteo.PendienteEnvio)]
    public void ElResumenEsElMasAtrasadoIgnorandoLosDescartados(EstadoCosteo[] estados, EstadoCosteo esperado) =>
        clsResumenEstadoCosteo.Resumir(estados.Select(e => (EstadoCosteo?)e)).Should().Be(esperado);

    [Fact]
    public void SinEstadosElResumenEsNuloYUnNuloMezcladoCuentaComoPorEnviar()
    {
        clsResumenEstadoCosteo.Resumir([]).Should().BeNull("sin camiones (o sin rutas) no hay costeo en Nexo");
        clsResumenEstadoCosteo.Resumir([null, null]).Should().BeNull("todos nulos: la cotización no pasa por Nexo (chat)");
        clsResumenEstadoCosteo.Resumir([EstadoCosteo.Autocosteo, null]).Should().Be(EstadoCosteo.PendienteEnvio,
            "un camión que todavía no viajó a Nexo deja al conjunto por enviar");
        clsResumenEstadoCosteo.Resumir([EstadoCosteo.Descartada, null]).Should().Be(EstadoCosteo.PendienteEnvio);
    }

    [Fact]
    public void ElEstadoSubeDeLosCamionesALaRutaYDeLasRutasALaCabecera()
    {
        var cotizacion = clsCotizacionMuestra.Licitacion(rutas: 2);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        var (ruta1, ruta2) = (cotizacion.RutasOrdenadas[0], cotizacion.RutasOrdenadas[1]);
        var segundo = ruta1.AgregarVehiculo("2S2", "FURGON", 5_000m);

        cotizacion.Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.PendienteCosteo || v.EstadoCosteo == null);
        ruta1.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000m, null);
        segundo.RegistrarCosteo(EstadoCosteo.Autocosteo, 800m, null);
        ruta2.Vehiculos[0].RegistrarCosteo(EstadoCosteo.PendienteCosteo, null, null);

        cotizacion.ResumirRutas().Should().Be(EstadoCosteo.PendienteCosteo, "la ruta 2 sigue en la Mesa");
        ruta1.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        ruta2.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);

        ruta2.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Entregada, 1_200m, null);
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.PendienteCosteo, "COT-1", null).Should().BeTrue();
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada, "autocosteo + entregada resume en entregada, aunque Nexo derive otra cosa");
    }

    [Fact]
    public void LosEstadosDeLaSolicitudLleganACadaCamion()
    {
        var cotizacion = clsCotizacionMuestra.Licitacion(rutas: 2);
        cotizacion.Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.PendienteEnvio, "lo guardado queda por enviar");

        cotizacion.RegistrarErrorEnvioCosteo("Nexo no respondió");
        cotizacion.Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.ErrorEnvio);
        cotizacion.Rutas.Should().OnlyContain(r => r.EstadoCosteo == EstadoCosteo.ErrorEnvio);

        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        cotizacion.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000m, null);
        cotizacion.VencerPollingCosteo("72 h").Should().BeTrue();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        cotizacion.Vehiculos.Select(v => v.EstadoCosteo).Should().Equal(
            [EstadoCosteo.Autocosteo, EstadoCosteo.CosteoVencido], "el camión con flete conserva su estado; el que no tenía, se vence");
    }

    // ------------------------------------------------------------------ §9.1 Totales, levante y precio

    [Fact]
    public void ElLevanteDeLaRutaSeCopiaACadaCamion()
    {
        var solicitud = clsSolicitudCosteoMuestra.Crear() with
        {
            LevanteContenedorDiferente = true,
            CiudadLevanteContenedor = "Cartagena",
            ValorAprovisionamiento = 350_000m,
        };
        var cotizacion = clsCotizacionMuestra.Formulario(solicitud: solicitud);
        var ruta = cotizacion.PrimeraRuta!;

        ruta.AgregarVehiculo("2S2", "FURGON", 1_000m);

        ruta.ValorLevanteContenedor.Should().Be(350_000m);
        ruta.Vehiculos.Should().HaveCount(2).And.OnlyContain(v => v.CostoLevanteContenedor == 350_000m, "decisión 4: un levante por camión");
        ruta.CostoLevanteContenedorTotal.Should().Be(700_000m);
        ruta.CiudadLevanteNombre.Should().Be("Cartagena", "decisión 2: se copia el nombre del municipio");
        ruta.ALineaNexo(ruta.Vehiculos[1]).ValorAprovisionamiento.Should().Be(350_000m, "a Nexo viaja el levante de ESE camión");
    }

    [Fact]
    public void SinLevanteElCamionLlevaCero()
    {
        var camion = clsCotizacionMuestra.Formulario().PrimeraRuta!.Vehiculos.Single();

        camion.CostoLevanteContenedor.Should().Be(0m);
    }

    [Fact]
    public void LosTotalesDeLaRutaSumanSusCamionesYSusConexosYElPrecioUsaSuMargen()
    {
        var cotizacion = clsCotizacionMuestra.Formulario(solicitud: clsSolicitudCosteoMuestra.Crear() with
        {
            LevanteContenedorDiferente = true, CiudadLevanteContenedor = "Cartagena", ValorAprovisionamiento = 100_000m,
        });
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AgregarVehiculo("2S2", "FURGON", 1_000m);
        ruta.RegistrarMargenCosteo(0.15m, 0.20m, OrigenMargen.PoliticaLinea, "margen");
        ruta.AgregarOActualizarConexo("CX-1", "Cargue", 50_000m, 2m).Should().BeTrue();
        ruta.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000_000m, 200_000m);
        ruta.Vehiculos[1].RegistrarCosteo(EstadoCosteo.Autocosteo, 500_000m, null);

        cotizacion.RecalcularTotales(_ahora).Valor.Should().BeTrue();

        ruta.CostoFletesTotal.Should().Be(1_500_000m);
        ruta.CostoRetornoContenedorTotal.Should().Be(200_000m, "un retorno que Nexo no trae cuenta 0");
        ruta.CostoLevanteContenedorTotal.Should().Be(200_000m, "100.000 por camión");
        ruta.CostoServiciosConexos.Should().Be(100_000m, "precio × cantidad");
        ruta.CostoFinal.Should().Be(2_000_000m);
        ruta.PrecioFinalLinea.Should().Be(2_500_000m, "2.000.000 / (1 - 0,20)");
        cotizacion.PrecioFinal.Should().Be(2_500_000m);
        cotizacion.CostoFinal.Should().Be(2_000_000m);
        cotizacion.CosteoListoAt.Should().Be(_ahora);
        cotizacion.RecalcularTotales(_ahora.AddHours(1)).Valor.Should().BeFalse("recalcular sin cambios no cambia nada");
        cotizacion.CosteoListoAt.Should().Be(_ahora, "costeoListoAt es la primera vez que hubo precio");
    }

    [Fact]
    public void MientrasUnCamionNoTengaFleteLaRutaNoTienePrecioNiLaCabecera()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AgregarVehiculo(null, null, 1_000m);
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        ruta.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000_000m, null);

        cotizacion.RecalcularTotales(_ahora);

        ruta.Vehiculos[1].TipoVehiculo.Should().BeNull("sin selección automática: lo resuelve la Mesa de Costeo");
        (ruta.CostoFletesTotal, ruta.CostoFinal, ruta.PrecioFinalLinea).Should().Be(((decimal?)null, (decimal?)null, (decimal?)null));
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.CosteoListoAt.Should().BeNull();
    }

    [Fact]
    public void LaCabeceraSumaSusRutasSoloCuandoTodasTienenPrecio()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 850_000m]);

        cotizacion.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(1_176_471m, 1_000_000m);
        cotizacion.PrecioFinal.Should().Be(2_176_471m);

        cotizacion.RutasOrdenadas[1].Vehiculos[0].RegistrarCosteo(EstadoCosteo.PendienteCosteo, null, null);
        cotizacion.RecalcularTotales(_ahora);

        cotizacion.RutasOrdenadas[0].PrecioFinalLinea.Should().Be(1_176_471m, "la ruta con flete conserva su precio");
        cotizacion.PrecioFinal.Should().BeNull("una ruta sin precio deja a la solicitud sin total");
    }

    [Fact]
    public void ElPrecioEnDolaresSeConvierteConLaTrmFijada()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 4_000_000m, margen: 0.20m, moneda: "USD", trm: 4_000m);

        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(1_250m, "4.000.000 / 0,8 = 5.000.000 COP ÷ 4.000");
        cotizacion.PrecioFinal.Should().Be(1_250m);
    }

    [Fact]
    public void SinMargenLaRutaTieneCostoPeroNoPrecio()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000_000m, null);

        cotizacion.RecalcularTotales(_ahora).EsExitoso.Should().BeTrue();

        cotizacion.PrimeraRuta!.CostoFinal.Should().Be(1_000_000m);
        cotizacion.PrimeraRuta.PrecioFinalLinea.Should().BeNull();
    }

    [Fact]
    public void UnPrecioQueElModuloRechazaDejaLaRutaSinPrecioYDevuelveElError()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        cotizacion.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 0m, null);

        var recalculo = cotizacion.RecalcularTotales(_ahora);

        recalculo.Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido, "el módulo de precio exige un costo positivo");
        recalculo.Error.Mensaje.Should().StartWith("Ruta 1:");
        cotizacion.PrimeraRuta.PrecioFinalLinea.Should().BeNull();
        cotizacion.PrecioFinal.Should().BeNull();
    }

    [Fact]
    public void LosConexosSeAgreganActualizanYQuitanPorSuId()
    {
        var ruta = clsCotizacionMuestra.Formulario().PrimeraRuta!;

        ruta.AgregarOActualizarConexo("CX-1", "Cargue", 10_000m, 1m).Should().BeTrue();
        ruta.AgregarOActualizarConexo("cx-1", null, 10_000m, 1m).Should().BeFalse("el mismo conexo con los mismos datos no cambia nada");
        ruta.AgregarOActualizarConexo("CX-1", null, 12_000m, 1.5m).Should().BeTrue();
        ruta.AgregarOActualizarConexo("CX-2", "Seguro", 5_000m, 1m);

        ruta.Conexos.Single(c => c.IdServicioConexo == "CX-1").Should().Match<clsServicioConexoRuta>(c => c.Nombre == "Cargue" && c.Costo == 18_000m);
        ruta.ConservarConexos(new HashSet<string> { "CX-2" }).Select(c => c.IdServicioConexo).Should().Equal("CX-1");
        ruta.Conexos.Should().ContainSingle(c => c.IdServicioConexo == "CX-2");
        FluentActions.Invoking(() => ruta.AgregarOActualizarConexo("CX-3", null, 1m, 0m)).Should().Throw<ArgumentOutOfRangeException>("la cantidad es mayor que 0");
    }

    // ------------------------------------------------------------------ Margen por ruta (adaptación: el ajuste aplica a todas)

    [Fact]
    public void ElAjusteDeLaSolicitudAplicaElMismoMargenATodasSusRutasYRecalculaElTotal()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 850_000m]);

        cotizacion.ValidarAjusteMargen(0.20m, null).Should().BeNull();
        cotizacion.AplicarMargenNegociado(0.20m, _ahora).Valor.Should().BeTrue();

        cotizacion.Rutas.Should().OnlyContain(r => r.MargenAplicado == 0.20m && r.OrigenMargen == OrigenMargen.Negociado);
        cotizacion.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(1_250_000m, 1_062_500m);
        cotizacion.PrecioFinal.Should().Be(2_312_500m);
        cotizacion.TieneMargenNegociado.Should().BeTrue();
    }

    [Fact]
    public void SinCosteoEnAlgunaRutaNoSeAjusta()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m]);
        cotizacion.AgregarRuta(new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", PesoKg = 1m }, 1, 3);

        cotizacion.ValidarAjusteMargen(0.20m, null)!.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }

    // ------------------------------------------------------------------ Chat, mercancías y re-solicitud

    [Fact]
    public void LaCotizacionDelChatTieneUnaRutaConMargenYPrecioYUnCamionConElFlete()
    {
        var cotizacion = clsCotizacionMuestra.Chat(costo: 2_000_000m, margen: 0.15m, precio: 2_352_941m);
        var ruta = cotizacion.PrimeraRuta!;

        (ruta.Orden, ruta.RutaOrigenIndice, ruta.MargenAplicado, ruta.PrecioFinalLinea).Should().Be((1, 0, 0.15m, 2_352_941m));
        ruta.Vehiculos.Should().ContainSingle().Which.CostoFlete.Should().Be(2_000_000m);
        ruta.CostoFinal.Should().Be(2_000_000m);
        cotizacion.PrecioFinal.Should().Be(2_352_941m);
        cotizacion.EstadoCosteo.Should().BeNull("el chat no pasa por Nexo");
        cotizacion.ResumirRutas().Should().BeNull();
    }

    [Fact]
    public void UnaMercanciaNuevaExigeElTipoDelCubicador()
    {
        var posicion = new clsPosicionCarga("Naranja", 10, 100m, 80m, 60m, 20m);

        clsMercancia.Crear(Guid.NewGuid(), 1, null, posicion).Error.Mensaje.Should().Contain("id_tipo_mercancia");
        clsMercancia.Crear(Guid.NewGuid(), 1, Guid.Empty, posicion).EsFallo.Should().BeTrue();
        var mercancia = clsMercancia.Crear(Guid.NewGuid(), 2, Guid.NewGuid(), posicion).Valor;
        (mercancia.Orden, mercancia.NombreMercancia, mercancia.APosicion().IdTipoMercancia).Should().Be((2, "Naranja", mercancia.IdTipoMercancia));
        clsMercancia.Crear(Guid.NewGuid(), 1, Guid.NewGuid(), posicion with { Cantidad = 0 }).EsFallo.Should().BeTrue("las reglas de la posición de Nexo siguen");
    }

    [Fact]
    public void LaReSolicitudOlvidaLosCostosDeNexoPeroConservaElLevanteYLosConexos()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(estado: EstadoCosteo.Entregada);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AgregarOActualizarConexo("CX-1", "Cargue", 10_000m, 1m);

        cotizacion.PrepararReSolicitud();

        ruta.Vehiculos.Should().OnlyContain(v => v.CostoFlete == null && v.CostoRetornoContenedor == null && v.LineaSolicitudNexoId == null);
        ruta.Vehiculos[0].CostoLevanteContenedor.Should().Be(0m);
        ruta.Conexos.Should().ContainSingle();
        (ruta.CostoFinal, ruta.PrecioFinalLinea, ruta.EstadoCosteo).Should().Be(((decimal?)null, (decimal?)null, EstadoCosteo.PendienteEnvio));
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
    }

    [Fact]
    public void UnaLineaANexoPorCamionConElPesoDelCamionYLasMercanciasSoloConUnCamion()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.LineasNexo().Should().ContainSingle().Which.Linea.Posiciones.Should().HaveCount(2, "con un camión viajan las mercancías");

        cotizacion.PrimeraRuta!.AgregarVehiculo("2S2", "FURGON", 1_000m);
        var lineas = cotizacion.LineasNexo();

        lineas.Should().HaveCount(2);
        lineas.Select(l => l.Linea.PesoKg).Should().BeEquivalentTo([28_500.5m, 1_000m]);
        lineas.Should().OnlyContain(l => l.Linea.Posiciones.Count == 0, "con varios camiones a Nexo le basta el peso de cada uno");
        lineas.Should().OnlyContain(l => l.Linea.Origen == "Buenaventura" && l.Linea.Destino == "Bogotá D.C.");
    }
}
