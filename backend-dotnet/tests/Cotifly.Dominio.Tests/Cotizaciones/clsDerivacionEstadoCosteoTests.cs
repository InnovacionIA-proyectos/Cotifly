using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Derivación de <c>estado_costeo</c> (HU-005.7, ADR-015 §4): la misma regla de la bandeja de Nexo, que cruza las líneas de la
/// solicitud contra las líneas de la cotización por <c>LineaSolicitudId</c>. Y las transiciones del polling en la cotización.
/// </summary>
public sealed class clsDerivacionEstadoCosteoTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SinFleteEnAlgunaLineaEsPendienteDeCosteo()
    {
        var foto = Foto("EnCosteo", ["L1", "L2"], Cotizacion("Borrador", Linea("L1")));

        clsDerivacionEstadoCosteo.Derivar(foto).Should().Be(EstadoCosteo.PendienteCosteo);
    }

    [Fact]
    public void ConTodasLasLineasCosteadasYLaCotizacionEnBorradorEsAutocosteo()
    {
        var foto = Foto("EnCosteo", ["L1", "L2"], Cotizacion("Borrador", Linea("l1"), Linea("L2")));

        clsDerivacionEstadoCosteo.Derivar(foto).Should().Be(EstadoCosteo.Autocosteo, "el cruce por LineaSolicitudId no distingue mayúsculas");
    }

    [Fact]
    public void ConLaCotizacionOLaSolicitudEntregadaEsEntregadaAunqueFaltenLineas()
    {
        clsDerivacionEstadoCosteo.Derivar(Foto("EnCosteo", ["L1"], Cotizacion("Entregada"))).Should().Be(EstadoCosteo.Entregada);
        clsDerivacionEstadoCosteo.Derivar(Foto("Entregada", ["L1"], Cotizacion("Borrador"))).Should().Be(EstadoCosteo.Entregada);
    }

    [Fact]
    public void UnaSolicitudDescartadaEsDescartadaSinCotizacion() =>
        clsDerivacionEstadoCosteo.Derivar(Foto("Descartada", ["L1"], null)).Should().Be(EstadoCosteo.Descartada);

    [Fact]
    public void SinCotizacionOSinLineasNuncaEsAutocosteo()
    {
        clsDerivacionEstadoCosteo.Derivar(Foto("Pendiente", ["L1"], null)).Should().Be(EstadoCosteo.PendienteCosteo);
        clsDerivacionEstadoCosteo.Derivar(Foto("Pendiente", [], Cotizacion("Borrador", Linea("L1")))).Should().Be(EstadoCosteo.PendienteCosteo);
    }

    [Theory]
    [InlineData(EstadoCosteo.Entregada, true)]
    [InlineData(EstadoCosteo.Descartada, true)]
    [InlineData(EstadoCosteo.CosteoVencido, true)]
    [InlineData(EstadoCosteo.PendienteCosteo, false)]
    [InlineData(EstadoCosteo.Autocosteo, false)]
    public void LosEstadosTerminalesDetienenElPolling(EstadoCosteo estado, bool terminal) =>
        clsDerivacionEstadoCosteo.EsTerminal(estado).Should().Be(terminal);

    [Fact]
    public void ElCostoDeUnaLineaSumaFleteAprovisionamientoYRetorno() =>
        new clsLineaCosteoNexo("L1", 2_000_000m, 150_000m, 300_000m).Costo.Should().Be(2_450_000m);

    [Fact]
    public void UnaConsultaQueCambiaElEstadoReiniciaLosFallosYGuardaLaCotizacionDeNexo()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarFalloConsultaCosteo(_ahora, _ahora.AddSeconds(10));
        cotizacion.CosteoConsultasFallidas.Should().Be(1);

        var cambio = cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-9", _ahora.AddMinutes(1), _ahora.AddMinutes(2));

        cambio.Should().BeTrue();
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.CotizacionNexoId.Should().Be("COT-9");
        cotizacion.CosteoConsultasFallidas.Should().Be(0);
        cotizacion.CosteoUltimaConsultaAt.Should().Be(_ahora.AddMinutes(1));
        cotizacion.CosteoSiguienteConsultaAt.Should().Be(_ahora.AddMinutes(2));
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, null, _ahora.AddMinutes(2), null).Should().BeFalse("la misma foto no es un cambio");
        cotizacion.CotizacionNexoId.Should().Be("COT-9", "una respuesta sin id no borra el que ya se conocía");
    }

    [Fact]
    public void VencerElPollingSoloCambiaElEstadoSiNoHuboCosteo()
    {
        var sinCosteo = Enviada();
        sinCosteo.VencerPollingCosteo("Nexo no costeó en 72 h").Should().BeTrue();
        sinCosteo.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        sinCosteo.MotivoEstadoCosteo.Should().Be("Nexo no costeó en 72 h");
        sinCosteo.CosteoSiguienteConsultaAt.Should().BeNull();
        sinCosteo.SeConsultaEnNexo.Should().BeFalse();

        var conCosteo = Enviada();
        conCosteo.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _ahora, _ahora.AddMinutes(1));
        conCosteo.VencerPollingCosteo("x").Should().BeFalse();
        conCosteo.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        conCosteo.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public void UnaSolicitudQueNexoYaNoReconoceDejaDeConsultarseConElMotivo()
    {
        var cotizacion = Enviada();

        cotizacion.DetenerPorSolicitudDesconocida("Nexo ya no reconoce la solicitud").Should().BeTrue();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        cotizacion.MotivoEstadoCosteo.Should().Be("Nexo ya no reconoce la solicitud");
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public void UnaEntregadaSinPrecioNiPdfQuedaMarcadaParaRelecturaHastaQueTengaPrecio()
    {
        // HU-013.14 (ADR-018 §17): el rastro del resultado registrado antes de leer el costeo.
        var cotizacion = Enviada();
        cotizacion.EntregadaSinPrecio.Should().BeFalse("pendiente_costeo todavía no es entregada");

        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "COT-1", _ahora, null);
        cotizacion.EntregadaSinPrecio.Should().BeTrue();
        cotizacion.SeConsultaEnNexo.Should().BeFalse("entregada sigue siendo terminal para el polling normal");

        cotizacion.ProgramarRelecturaCosteo(_ahora.AddSeconds(1));
        cotizacion.CosteoSiguienteConsultaAt.Should().Be(_ahora.AddSeconds(1));
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada, "agendar la relectura no cambia el estado");
        cotizacion.VersionNegocio.Should().Be(0, "ni la versión de negocio");

        cotizacion.RegistrarPrecioCosteo(1_000_000m, 1_176_471m, null, _ahora);
        cotizacion.EntregadaSinPrecio.Should().BeFalse();
    }

    private static clsCotizacion Enviada()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-DG-28092026-001" }).Valor;
        cotizacion.RegistrarSolicitudCosteo(new clsSolicitudCosteo { Tipo = TipoSolicitud.Spot, Origen = "Cali", Destino = "Bogotá", PesoKg = 1_000m });
        cotizacion.RegistrarEnvioCosteo("SOL-1", costeoCompleto: false, _ahora, _ahora.AddSeconds(5));
        cotizacion.SeConsultaEnNexo.Should().BeTrue();
        return cotizacion;
    }

    private static clsCosteoConsultadoNexo Foto(string estado, IReadOnlyList<string> lineas, clsCotizacionNexo? cotizacion) => new(estado, lineas, cotizacion);

    private static clsCotizacionNexo Cotizacion(string estado, params clsLineaCosteoNexo[] lineas) => new("COT-1", estado, "SinDefinir", lineas, []);

    private static clsLineaCosteoNexo Linea(string id) => new(id, 1_000_000m, null, null);
}
