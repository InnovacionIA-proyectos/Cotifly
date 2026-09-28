using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>Formulario ampliado (HU-005.8): mismos límites que <c>CapturaSolicitudRequest</c> de Nexo.</summary>
public sealed class clsSolicitudCosteoTests
{
    [Fact]
    public void UnaSolicitudCompletaEsValida() => Valida().Validar().Should().BeNull();

    [Theory]
    [InlineData("", "Bogotá", "origen")]
    [InlineData("Cali", " ", "destino")]
    public void OrigenYDestinoSonObligatorios(string origen, string destino, string campo) =>
        (Valida() with { Origen = origen, Destino = destino }).Validar()!.Mensaje.Should().Contain(campo);

    [Fact]
    public void LosTextosNoSuperanLosLargosDeNexo()
    {
        (Valida() with { Origen = new string('a', 151) }).Validar().Should().NotBeNull();
        (Valida() with { ClaseContenedor = new string('a', 21) }).Validar()!.Mensaje.Should().Contain("clase_contenedor");
        (Valida() with { Referencia = new string('a', 201) }).Validar()!.Mensaje.Should().Contain("referencia");
    }

    [Fact]
    public void LasHorasYLosValoresRespetanLosRangosDeNexo()
    {
        (Valida() with { HorasCargue = 241m }).Validar()!.Mensaje.Should().Contain("horas_cargue");
        (Valida() with { PesoKg = -1m }).Validar()!.Mensaje.Should().Contain("peso_kg");
        (Valida() with { ValorMercancia = -0.01m }).Validar()!.Mensaje.Should().Contain("valor_mercancia");
        (Valida() with { HorasDescargue = 240m }).Validar().Should().BeNull();
    }

    [Fact]
    public void CadaPosicionSeValidaConSuNumero()
    {
        var solicitud = Valida() with { Posiciones = [new clsPosicionCarga(null, 1, 10m, 10m, 10m, null), new clsPosicionCarga(null, 0, 10m, 10m, 10m, null)] };
        solicitud.Validar()!.Mensaje.Should().Contain("posición 2");

        (Valida() with { Posiciones = [new clsPosicionCarga(null, 1, 0.05m, 10m, 10m, null)] }).Validar()!.Mensaje.Should().Contain("largo_cm");
    }

    [Fact]
    public void NoAdmiteMasDeDoscientasPosiciones()
    {
        var posiciones = Enumerable.Range(0, 201).Select(_ => new clsPosicionCarga(null, 1, 10m, 10m, 10m, null)).ToList();
        (Valida() with { Posiciones = posiciones }).Validar()!.Codigo.Should().Be(clsSolicitudCosteo.CodigoError);
    }

    [Fact]
    public void RegistrarLaSolicitudDejaLaCotizacionPendienteDeEnvioYLaReconstruyeIgual()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-DG-28092026-001" }).Valor;
        var solicitud = Valida() with { Referencia = "  OC-1  " };

        cotizacion.RegistrarSolicitudCosteo(solicitud);
        var posiciones = clsCotizacionPosicion.Desde(cotizacion.Id, solicitud.Posiciones);

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        posiciones.Select(p => p.Orden).Should().Equal(1, 2);
        cotizacion.SolicitudCosteo(posiciones).Should().BeEquivalentTo(solicitud with { Referencia = "OC-1" });
    }

    [Fact]
    public void RegistrarUnaSolicitudInvalidaLanza()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-DG-28092026-001" }).Valor;

        var registrar = () => cotizacion.RegistrarSolicitudCosteo(Valida() with { Origen = " " });

        registrar.Should().Throw<ArgumentException>();
        cotizacion.EstadoCosteo.Should().BeNull();
    }

    private static clsSolicitudCosteo Valida() => new()
    {
        Tipo = TipoSolicitud.Licitacion,
        Origen = "Cali",
        Destino = "Bogotá",
        TipoVehiculo = "2S3",
        Carroceria = "FURGON",
        HorasCargue = 4m,
        PesoKg = 12_000m,
        Posiciones = [new clsPosicionCarga("Caja", 3, 50m, 40m, 30m, 12.5m), new clsPosicionCarga(null, 1, 100m, 100m, 100m, null)],
    };
}
