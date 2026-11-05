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
    }

    [Fact]
    public void ClaseContenedorSoloConClaseDeViajeContenedorYObligatoriaConElla()
    {
        var contenedor = Valida() with { ClaseViaje = "Contenedor", ClaseContenedor = "CNT40.HC" };
        contenedor.Validar().Should().BeNull();
        (contenedor with { ClaseViaje = "Carga Suelta" }).Validar()!.Mensaje.Should().Contain("clase_contenedor").And.Contain("solo aplica");
        (contenedor with { ClaseViaje = null }).Validar()!.Mensaje.Should().Contain("clase_contenedor");
        (contenedor with { ClaseContenedor = null }).Validar()!.Mensaje.Should().Contain("clase_contenedor").And.Contain("obligatorio");
        (Valida() with { ClaseViaje = "Cupo" }).Validar().Should().BeNull();
    }

    [Fact]
    public void ValorAprovisionamientoSoloConUnLevanteDistintoDelOrigen()
    {
        (Valida() with { ValorAprovisionamiento = 100m }).Validar()!.Mensaje.Should().Contain("valor_aprovisionamiento");
        (Valida() with { ValorAprovisionamiento = 100m, CiudadLevanteContenedor = " cali " }).Validar()!.Mensaje.Should().Contain("valor_aprovisionamiento");
        (Valida() with { ValorAprovisionamiento = 100m, CiudadLevanteContenedor = "Buenaventura", LevanteContenedorDiferente = true }).Validar().Should().BeNull();
    }

    [Fact]
    public void NotasYMunicipiosRespetanLosLimitesDeNexo()
    {
        (Valida() with { Notas = new string('n', 2001) }).Validar()!.Mensaje.Should().Contain("notas");
        (Valida() with { OrigenMunicipioId = 0 }).Validar()!.Mensaje.Should().Contain("origen_municipio_id");
        (Valida() with { DestinoMunicipioId = -3 }).Validar()!.Mensaje.Should().Contain("destino_municipio_id");
        var completa = Valida() with { Notas = new string('n', 2000), OrigenMunicipioId = 1, DestinoMunicipioId = 2 };
        completa.Validar().Should().BeNull();
        completa.Encabezado.Notas.Should().HaveLength(2000);
        completa.ALinea().OrigenMunicipioId.Should().Be(1);
        clsSolicitudCosteo.Desde(completa.Encabezado, completa.ALinea()).Should().BeEquivalentTo(completa);
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
    public void SinPesoNiPosicionesConPesoEsPesoRequeridoComoEnNexo()
    {
        var error = (Valida() with { PesoKg = null, Posiciones = [new clsPosicionCarga("Bultos", 1, 0m, 0m, 0m, null)] }).Validar()!;

        error.Codigo.Should().Be(clsSolicitudCosteo.CodigoError);
        error.Detalle.Should().Be(clsSolicitudCosteo.PesoRequerido);
        (Valida() with { PesoKg = 0m, Posiciones = [] }).Validar()!.Detalle.Should().Be(clsSolicitudCosteo.PesoRequerido);
    }

    [Fact]
    public void SinPesoDeLaLineaSeUsaLaSumaDeLasPosiciones() =>
        (Valida() with { PesoKg = null }).Validar().Should().BeNull("3 × 4.000 kg = 12.000 kg");

    [Theory]
    [InlineData(12_121, null)]
    [InlineData(11_882, null)]
    [InlineData(12_200, clsSolicitudCosteo.PesoInconsistente)]
    [InlineData(11_800, clsSolicitudCosteo.PesoInconsistente)]
    public void ElPesoNoPuedeDiferirMasDelUnoPorCientoDeLaSumaDeLasPosiciones(int peso, string? detalle) =>
        (Valida() with { PesoKg = peso }).Validar()?.Detalle.Should().Be(detalle);

    [Fact]
    public void UnRechazoDefinitivoDeNexoNoSeReenvia()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-DG-28092026-002" }).Valor;
        cotizacion.RegistrarSolicitudCosteo(Valida(), 3);

        cotizacion.RegistrarErrorEnvioCosteo("Nexo rechazó los datos (HTTP 400 PESO_INCONSISTENTE)", definitivo: true);

        cotizacion.ErrorEnvioDefinitivo.Should().BeTrue();
        cotizacion.MotivoEstadoCosteo.Should().StartWith(clsCotizacionSolicitud.PrefijoRechazoDefinitivo);
        cotizacion.PuedeReenviarCosteo(DateTimeOffset.MaxValue).Should().BeFalse();
        cotizacion.RegistrarErrorEnvioCosteo("Nexo caído");
        cotizacion.PuedeReenviarCosteo(DateTimeOffset.MaxValue).Should().BeTrue();
    }

    [Fact]
    public void LosNumerosAdmitenMaximoDosDecimalesComoLasColumnasDelEsquema()
    {
        (Valida() with { PesoKg = 12_000.005m }).Validar()!.Mensaje.Should().Contain("peso_kg").And.Contain("2 decimales");
        (Valida() with { HorasCargue = 4.125m }).Validar()!.Mensaje.Should().Contain("horas_cargue");
        (Valida() with { Posiciones = [new clsPosicionCarga(null, 1, 10.001m, 10m, 10m, null)] }).Validar()!.Mensaje.Should().Contain("largo_cm");
        (Valida() with { PesoKg = 12_000.500m }).Validar().Should().BeNull("los ceros a la derecha no son decimales de más");
    }

    [Fact]
    public void CadaPosicionSeValidaConSuNumero()
    {
        var solicitud = Valida() with { Posiciones = [new clsPosicionCarga(null, 1, 10m, 10m, 10m, 1m), new clsPosicionCarga(null, 0, 10m, 10m, 10m, 1m)] };
        solicitud.Validar()!.Mensaje.Should().StartWith("Mercancía 2: la cantidad", "D6: la mercancía en base 1, sin índices");

        (Valida() with { Posiciones = [new clsPosicionCarga(null, 1, 0.05m, 10m, 10m, null)] }).Validar()!.Mensaje.Should().Contain("largo_cm");
    }

    [Fact]
    public void NoAdmiteMasDeDoscientasPosiciones()
    {
        var posiciones = Enumerable.Range(0, 201).Select(_ => new clsPosicionCarga(null, 1, 10m, 10m, 10m, null)).ToList();
        (Valida() with { Posiciones = posiciones }).Validar()!.Codigo.Should().Be(clsSolicitudCosteo.CodigoError);
    }

    [Fact]
    public void RegistrarLaSolicitudDejaLaRutaYSuCamionPendientesDeEnvioYLaReconstruyeIgual()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-DG-28092026-001" }).Valor;
        var solicitud = Valida();

        var ruta = cotizacion.RegistrarSolicitudCosteo(solicitud, 3);

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        cotizacion.Rutas.Should().ContainSingle().Which.Should().BeSameAs(ruta);
        (ruta.Orden, ruta.RutaOrigenIndice, ruta.LineaNegocioId, ruta.EstadoCosteo).Should().Be((1, 0, 3, EstadoCosteo.PendienteEnvio));
        ruta.Vehiculos.Should().ContainSingle().Which.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        ruta.MercanciasOrdenadas.Select(p => p.Orden).Should().Equal(1, 2);
        clsSolicitudCosteo.Desde(cotizacion.EncabezadoSolicitud!, ruta.ALinea())
            .Should().BeEquivalentTo(
                solicitud with { RetornaContenedor = solicitud.RetornaContenedor ?? false, LevanteContenedorDiferente = solicitud.LevanteContenedorDiferente ?? false },
                "lo guardado (cabecera + ruta + camión + mercancías) reconstruye el formulario; retorno y levante ya no aceptan vacío (0009)");
    }

    [Fact]
    public void RegistrarUnaSolicitudInvalidaLanza()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-DG-28092026-001" }).Valor;

        var registrar = () => cotizacion.RegistrarSolicitudCosteo(Valida() with { Origen = " " }, 3);

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
        HorasDescargue = 4m,
        PesoKg = 12_000m,
        Posiciones = [new clsPosicionCarga("Caja", 3, 50m, 40m, 30m, 4_000m), new clsPosicionCarga(null, 1, 100m, 100m, 100m, 0.01m)],
    };
}
