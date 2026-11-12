using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Con la cotización finalizada el polling NO cambia el precio guardado (mismo mecanismo que el PDF generado, ADR-015 §5):
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
    public void UnaFinalizadaConLaFormulaAnteriorDeConexosNoSeRecalculaConLaNueva()
    {
        // La cotización se finalizó con la fórmula anterior ((flete + conexos) con margen); Nexo ya manda la venta de los
        // conexos. El polling no la recalcula: conserva su precio y sus costos.
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([2_000_000m], usuarioId: _ana, tipo: TipoSolicitud.Spot);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.FijarPrecio(2_452_941m);   // (2.000.000 + 85.000) / 0,85: la fórmula anterior
        cotizacion.RecalcularPrecio(_ahora);
        cotizacion.Finalizar(_ana, _ahora).Valor.Should().BeTrue();
        var conVenta = new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 85_000m) { PrecioVentaUnitario = 120_000m, ValorVenta = 120_000m, CotizacionLineaId = "CL1" };

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_000_000m], conexos: [conVenta], estadoCotizacion: "Entregada"), _ahora.AddMinutes(5));

        (ruta.PrecioFinalLinea, cotizacion.PrecioFinal).Should().Be(((decimal?)2_452_941m, (decimal?)2_452_941m), "el precio finalizado se conserva");
        ruta.Conexos.Single().PrecioVenta.Should().Be(85_000m, "el conexo guardado no cambia");
        ruta.CostoFinal.Should().Be(2_085_000m);
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

    // ------------------------------------------------------------------ El aviso se quita y la ruta rechazada después

    [Fact]
    public void SiNexoVuelveAlCostoFinalizadoElAvisoSeQuitaUnaSolaVez()
    {
        var cotizacion = Finalizada(2_000_000m);
        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_500_000m]), _ahora.AddMinutes(5)).Should().BeTrue();
        cotizacion.MotivoEstadoCosteo.Should().StartWith(clsPrecioCosteoService.PrefijoCambioDespuesDeFinalizar);

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_000_000m]), _ahora.AddMinutes(10)).Should().BeTrue("quitar el aviso es un cambio visible");

        cotizacion.MotivoEstadoCosteo.Should().BeNull("Nexo volvió al costo con que se finalizó: el aviso ya no aplica");
        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_000_000m]), _ahora.AddMinutes(15)).Should().BeFalse("ya no hay nada que quitar");
    }

    [Fact]
    public void SinDiferenciasNoSeQuitaUnMotivoQueNoEsElAvisoDeLaFinalizacion()
    {
        var cotizacion = Finalizada(2_000_000m);
        cotizacion.AdvertirCosteo("Otro aviso del costeo.");

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_000_000m]), _ahora.AddMinutes(5)).Should().BeFalse();

        cotizacion.MotivoEstadoCosteo.Should().Be("Otro aviso del costeo.");
        cotizacion.QuitarAvisoCosteo(clsPrecioCosteoService.PrefijoCambioDespuesDeFinalizar).Should().BeFalse();
    }

    [Fact]
    public void SiLaMesaRechazaUnaRutaDespuesDeFinalizarElTotalSeConservaSeAvisaYElPdfPideVersionNueva()
    {
        var cotizacion = Finalizada(1_000_000m, 2_000_000m);
        var precio = cotizacion.PrecioFinal;
        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP").Should().BeNull();

        Aplicar(cotizacion, FotoConRechazo(rechazada: true), _ahora.AddMinutes(5)).Should().BeTrue();

        cotizacion.RutasOrdenadas[1].Rechazada.Should().BeTrue();
        cotizacion.PrecioFinal.Should().Be(precio, "el total finalizado no cambia aunque la ruta 2 ya no cuente");
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoCambioDespuesDeFinalizar([cotizacion.RutasOrdenadas[1]]));
        var pdf = clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP")!;
        pdf.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        pdf.Mensaje.Should().Contain("cambió después de finalizar").And.NotContain("espera la siguiente consulta");
        pdf.Datos!["accion"].Should().Be("nueva_version", "ninguna lectura del costeo va a cuadrar el total congelado");

        // La Mesa deshace el rechazo: el aviso se quita y el PDF vuelve a salir con el precio finalizado.
        Aplicar(cotizacion, FotoConRechazo(rechazada: false), _ahora.AddMinutes(10)).Should().BeTrue();

        cotizacion.RutasOrdenadas[1].Rechazada.Should().BeFalse();
        cotizacion.MotivoEstadoCosteo.Should().BeNull();
        cotizacion.PrecioFinal.Should().Be(precio);
        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP").Should().BeNull();
    }

    // ------------------------------------------------------------------ Costo bruto de la ruta con el precio congelado

    [Fact]
    public void ConLaCotizacionFinalizadaElCostoBrutoDeLaRutaEsElGuardadoYNoElNuevoDeNexo()
    {
        var cotizacion = Finalizada(2_000_000m);
        var costo = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single().CostoFinal;
        costo.Should().Be(cotizacion.PrimeraRuta!.CostoFinal).And.NotBeNull();

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([2_500_000m], estadoCotizacion: "Entregada"), _ahora.AddMinutes(5)).Should().BeTrue();

        var ruta = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Single();
        ruta.CostoFinal.Should().Be(costo, "el costo bruto acompaña al precio congelado: es el costo con que se finalizó, no el nuevo de Nexo");
        ruta.PrecioFinalLinea.Should().Be(cotizacion.PrimeraRuta.PrecioFinalLinea);
    }

    [Fact]
    public void ConUnaRutaRechazadaDespuesDeFinalizarElCostoBrutoSigueAlPrecioCongelado()
    {
        var cotizacion = Finalizada(1_000_000m, 2_000_000m);
        var antes = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas.Select(r => (r.PrecioFinalLinea, r.CostoFinal)).ToList();

        Aplicar(cotizacion, FotoConRechazo(rechazada: true), _ahora.AddMinutes(5)).Should().BeTrue();

        var despues = clsCosteoCotizacionService.Vista(cotizacion, null).Rutas;
        despues[1].EstadoCosteo.Should().Be("rechazada");
        despues.Select(r => (r.PrecioFinalLinea, r.CostoFinal)).Should().Equal(antes,
            "costo_final sale en los mismos casos que precio_final_linea: con el precio congelado los dos se conservan");
    }

    /// <summary>Dos líneas costeadas (<c>L1</c> y <c>L2</c>) con los mismos fletes; <c>L2</c> rechazada por la Mesa o ya no.</summary>
    private static clsCosteoConsultadoNexo FotoConRechazo(bool rechazada) =>
        new("EnCosteo", ["L1", "L2"],
            new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir",
                [new clsLineaCosteoNexo("L1", 1_000_000m, null, null, "CL1"), new clsLineaCosteoNexo("L2", 2_000_000m, null, null, "CL2")], []))
        {
            Detalle =
            [
                new clsLineaSolicitudNexo("L1", "A → B", null, null, null, null) { EstadoLinea = "porCostear" },
                new clsLineaSolicitudNexo("L2", "C → D", null, null, null, null)
                {
                    EstadoLinea = rechazada ? "rechazada" : "porCostear",
                    MotivoRechazo = rechazada ? "Sin cupo" : null,
                },
            ],
        };

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
