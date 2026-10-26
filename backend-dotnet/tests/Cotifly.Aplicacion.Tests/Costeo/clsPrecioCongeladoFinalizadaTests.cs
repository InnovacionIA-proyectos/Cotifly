using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-036.4 (#208): con la cotización finalizada el polling NO cambia el precio guardado (mismo mecanismo que el PDF generado, ADR-015 §5):
/// el <c>estado_costeo</c> sigue avanzando con Nexo, lo que trajo Nexo se audita (<c>costeo_nexo_precio_congelado</c>, una vez por aviso) y
/// <c>motivoEstadoCosteo</c> dice qué rutas cambiaron, sin montos.
/// </summary>
public sealed class clsPrecioCongeladoFinalizadaTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;
    private static readonly Guid _ana = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly clsPrecioCosteoService _servicio = new();

    private bool Aplicar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset? cuando = null, clsConexosPendientesNexo? pendientes = null) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, cuando ?? _ahora, new clsOpcionesCosteoNexo(), _servicio, pendientes);

    private static clsCotizacionSolicitud Finalizada(params decimal[] fletes)
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada(fletes, usuarioId: _ana);
        cotizacion.Finalizar(_ana, _ahora).Valor.Should().BeTrue();
        return cotizacion;
    }

    [Fact]
    public void LaMesaEntregaConOtroValorElPrecioSeConservaElEstadoAvanzaYElMotivoDiceLaRuta()
    {
        var cotizacion = Finalizada(2_000_000m);
        var precio = cotizacion.PrecioFinal;

        var cambio = Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_500_000m], estadoCotizacion: "Entregada"), _ahora.AddMinutes(5));

        cambio.Should().BeTrue();
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada, "el estado sigue avanzando con Nexo");
        cotizacion.PrecioFinal.Should().Be(precio, "el precio finalizado se conserva");
        cotizacion.Vehiculos[0].CostoFlete.Should().Be(2_000_000m, "el camión conserva el costo con que se finalizó");
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoCambioDespuesDeFinalizar([cotizacion.RutasOrdenadas[0]]));
        cotizacion.MotivoEstadoCosteo.Should().StartWith(clsPrecioCosteoService.PrefijoCambioDespuesDeFinalizar)
            .And.Contain($"la ruta 1 ({cotizacion.RutasOrdenadas[0].Ruta})").And.NotContain("2.500.000").And.NotContain("2500000");
    }

    [Fact]
    public void LaMismaDiferenciaEnLaSiguienteVueltaNoVuelveAContarComoCambio()
    {
        var cotizacion = Finalizada(2_000_000m);
        var foto = clsCotizacionMuestra.Foto([2_500_000m]);
        Aplicar(cotizacion, foto, _ahora.AddMinutes(5)).Should().BeTrue("el aviso es nuevo");

        Aplicar(cotizacion, foto, _ahora.AddMinutes(10)).Should().BeFalse("la misma diferencia no vuelve a avisar ni sube la versión");

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
    }

    [Fact]
    public void SinDiferenciasNoHayAvisoYLaEntregaSoloCambiaElEstado()
    {
        var cotizacion = Finalizada(2_000_000m);
        var precio = cotizacion.PrecioFinal;

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_000_000m], estadoCotizacion: "Entregada"), _ahora.AddMinutes(5)).Should().BeTrue();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.PrecioFinal.Should().Be(precio);
        cotizacion.MotivoEstadoCosteo.Should().BeNull();
    }

    [Fact]
    public void ConVariasRutasElMotivoNombraSoloLasQueCambiaron()
    {
        var cotizacion = Finalizada(1_000_000m, 2_000_000m, 3_000_000m);
        var precio = cotizacion.PrecioFinal;

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([1_000_000m, 2_100_000m, 3_000_000m]), _ahora.AddMinutes(5));

        cotizacion.PrecioFinal.Should().Be(precio);
        cotizacion.MotivoEstadoCosteo.Should().Contain($"la ruta 2 ({cotizacion.RutasOrdenadas[1].Ruta})")
            .And.NotContain("ruta 1").And.NotContain("ruta 3");

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([1_100_000m, 2_100_000m, 3_300_000m]), _ahora.AddMinutes(10)).Should().BeTrue("cambiaron otras rutas");

        cotizacion.MotivoEstadoCosteo.Should().Contain(
            $"las rutas 1 ({cotizacion.RutasOrdenadas[0].Ruta}), 2 ({cotizacion.RutasOrdenadas[1].Ruta}) y 3 ({cotizacion.RutasOrdenadas[2].Ruta})");
        cotizacion.PrecioFinal.Should().Be(precio);
    }

    [Fact]
    public void LosCambiosTraenLoGuardadoYLoDeNexoPorCamionParaLaAuditoria()
    {
        var cotizacion = Finalizada(1_000_000m, 2_000_000m);
        var foto = clsCotizacionMuestra.Foto([1_000_000m, 2_400_000m], retorno: 50_000m);
        Aplicar(cotizacion, foto, _ahora.AddMinutes(5));

        var cambios = clsPrecioCosteoService.CambiosConPrecioCongelado(cotizacion, foto);

        cambios.Should().HaveCount(2, "el retorno nuevo también es un cambio de costo");
        var ruta2 = cambios.Single(c => c.Ruta.Orden == 2);
        (ruta2.FleteGuardado, ruta2.FleteNexo, ruta2.RetornoNexo, ruta2.Rechazado).Should().Be(((decimal?)2_000_000m, (decimal?)2_400_000m, (decimal?)50_000m, false));
    }

    [Fact]
    public void ConLaCotizacionSinFinalizarNiPdfElPrecioSiguePorElCosteoNuevo()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([2_000_000m], usuarioId: _ana);
        var precio = cotizacion.PrecioFinal;

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_500_000m]), _ahora.AddMinutes(5)).Should().BeTrue();

        cotizacion.PrecioFinal.Should().NotBe(precio, "sin finalizar el polling recalcula el precio (como siempre)");
        cotizacion.MotivoEstadoCosteo.Should().BeNull();
    }

    [Fact]
    public void ConLaCotizacionFinalizadaNoSeImportanLosConexosPendientesDeNexo()
    {
        var cotizacion = Finalizada(2_000_000m);
        var precio = cotizacion.PrecioFinal;
        cotizacion.MarcarConexosPendientesDeLeer();
        var pendientes = new clsConexosPendientesNexo(cotizacion.Folio, cotizacion.PrimeraRuta!.Id, []);

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_000_000m], conexos: [new clsConexoNexo("CX-9", "Cargue", "logistico", 1m, 170_000m)]),
            _ahora.AddMinutes(5), pendientes);

        cotizacion.PrimeraRuta.Conexos.Should().BeEmpty("el conexo cambiaría el precio finalizado");
        cotizacion.PrecioFinal.Should().Be(precio);
    }

    // ------------------------------------------------------------------ Polling: la auditoría del valor que trajo Nexo

    [Fact]
    public async Task ElPollingAuditaUnaVezLoQueTrajoNexoYSubeLaVersionUnaSolaVez()
    {
        var cotizaciones = Substitute.For<IClsCotizacionRepository>();
        var nexo = Substitute.For<IClsCosteoNexo>();
        var unidad = Substitute.For<IClsUnitOfWork>();
        var auditoria = Substitute.For<IClsAuditoriaService>();
        var reloj = new clsRelojFalso();
        var cotizacion = clsCotizacionMuestra.Formulario(usuarioId: _ana);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, reloj.AhoraUtc, reloj.AhoraUtc.AddSeconds(5));
        cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        clsCotizacionMuestra.AplicarCosteo(cotizacion, [2_000_000m], EstadoCosteo.Autocosteo, null);
        cotizacion.Finalizar(_ana, reloj.AhoraUtc).Valor.Should().BeTrue();
        var precio = cotizacion.PrecioFinal;
        var version = cotizacion.VersionNegocio;
        reloj.Avanzar(TimeSpan.FromMinutes(1));
        cotizaciones.ReclamarConsultaCosteoAsync(cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        cotizaciones.ObtenerPorIdAsync(cotizacion.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        unidad.GuardarCambiosSiVersionAsync(cotizacion.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        nexo.ConsultarCosteoAsync(_ana, "SOL-1", Arg.Any<CancellationToken>()).Returns(clsCotizacionMuestra.Foto([2_600_000m]));
        var polling = new clsPollingCosteoService(cotizaciones, nexo, unidad, auditoria, reloj, Options.Create(new clsOpcionesCosteoNexo()),
            NullLogger<clsPollingCosteoService>.Instance, _servicio);
        var pendiente = new clsConsultaCosteoPendiente(cotizacion.Id, reloj.AhoraUtc);

        (await polling.ProcesarAsync(pendiente, TestContext.Current.CancellationToken)).Should().Be(ResultadoConsultaCosteo.Cambio);
        (await polling.ProcesarAsync(pendiente, TestContext.Current.CancellationToken)).Should().Be(ResultadoConsultaCosteo.SinCambio);

        cotizacion.PrecioFinal.Should().Be(precio);
        cotizacion.VersionNegocio.Should().Be(version + 1, "el aviso nuevo es un cambio de negocio; la misma diferencia, no");
        await auditoria.Received(1).RegistrarAsync(clsPollingCosteoService.HerramientaPrecioCongelado,
            Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }
}
