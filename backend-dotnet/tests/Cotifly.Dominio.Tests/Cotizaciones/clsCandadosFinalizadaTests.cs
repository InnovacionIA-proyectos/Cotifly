using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Candados de la cotización finalizada en el agregado.
/// <list type="bullet">
/// <item>Parte A, después de finalizar: lo que cambia el precio (margen, conexos, re-solicitud en el mismo folio, levante, recálculo de
/// camiones) responde <c>PDF_YA_ENVIADO</c> como con la entregada (<see cref="clsCotizacionSolicitud.ValidarCambioEtapaFinal"/>) y la
/// edición pasa a <c>nueva_version</c>.</item>
/// <item>Parte B, antes de finalizar: el PDF, el correo y los textos (<see cref="clsCotizacionSolicitud.ValidarFinalizadaAntesDe"/>) y el
/// resultado (<see cref="clsCotizacionSolicitud.ValidarFinalizadaAntesDelResultado"/>) responden <c>COTIZACION_NO_FINALIZADA</c>.</item>
/// </list>
/// </summary>
public sealed class clsCandadosFinalizadaTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid _ana = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static clsCotizacionSolicitud Finalizada(EstadoCosteo estado = EstadoCosteo.Autocosteo)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(estado: estado, usuarioId: _ana);
        cotizacion.Finalizar(_ana, _ahora).Valor.Should().BeTrue();
        return cotizacion;
    }

    // ------------------------------------------------------------------ Parte A: después de finalizar

    [Fact]
    public void FinalizadaSinEntregarLaReglaDeLaEtapaFinalRespondePdfYaEnviadoYLaDeEntregaNo()
    {
        var cotizacion = Finalizada();

        var error = cotizacion.ValidarCambioEtapaFinal();

        error!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        error.Mensaje.Should().Be("La cotización ya está finalizada: crea una versión nueva para cambiarla.");
        cotizacion.ValidarEntregaAlCliente().Should().BeNull("el PDF y los textos siguen permitidos hasta entregarla");
    }

    [Fact]
    public void SinFinalizarNiEntregarLaReglaDeLaEtapaFinalNoBloquea()
    {
        clsCotizacionMuestra.Costeada().ValidarCambioEtapaFinal().Should().BeNull();
    }

    [Fact]
    public void EntregadaAlClienteYFinalizadaGanaElMensajeDeLaEntrega()
    {
        var cotizacion = Finalizada();
        cotizacion.RegistrarEnvio();

        cotizacion.ValidarCambioEtapaFinal()!.Mensaje.Should().Be("La cotización ya se envió al cliente: crea una versión nueva para cambiarla.");
    }

    [Fact]
    public void FinalizadaNoAdmiteAjusteDeMargen()
    {
        var cotizacion = Finalizada();

        cotizacion.ValidarAjusteMargen(1, 0.18m, null)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
    }

    [Fact]
    public void FinalizadaNoSeReSolicitaEnElMismoFolioPeroSiEnUnaVersionNueva()
    {
        var cotizacion = Finalizada(EstadoCosteo.Entregada);

        cotizacion.ValidarReSolicitud(versionNueva: false)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        cotizacion.ValidarReSolicitud(versionNueva: true).Should().BeNull();
    }

    [Fact]
    public void FinalizadaNoCambiaElLevanteNiLosCamiones()
    {
        var cotizacion = Finalizada();
        var precio = cotizacion.PrecioFinal;
        var plan = new clsPlanRuta([], null, null);

        cotizacion.CambiarValorLevante(1, 100_000m, _ahora).Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        cotizacion.ReemplazarCamionesDeRuta(cotizacion.PrimeraRuta!.Id, plan, _ahora).Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        cotizacion.PrecioFinal.Should().Be(precio);
    }

    [Fact]
    public void FinalizadaSeEditaEnUnaVersionNuevaComoLaEntregada()
    {
        var cotizacion = Finalizada();

        var modo = cotizacion.ModoEdicion(_ahora.AddHours(1));

        modo.Valor.Should().Be(ModoEdicionSolicitud.NuevaVersion);
    }

    [Fact]
    public void SinFinalizarConNexoLaEdicionSigueSiendoReemplazo()
    {
        var cotizacion = clsCotizacionMuestra.Costeada();

        cotizacion.ModoEdicion(_ahora.AddHours(1)).Valor.Should().Be(ModoEdicionSolicitud.Reemplazo);
    }

    // ------------------------------------------------------------------ Parte B: antes de finalizar

    [Theory]
    [InlineData("generar el PDF")]
    [InlineData("enviarla al cliente")]
    [InlineData("redactar los textos del PDF")]
    public void SinFinalizarElCierreRespondeCotizacionNoFinalizadaConLaLlamadaParaFinalizar(string accion)
    {
        var cotizacion = clsCotizacionMuestra.Costeada();

        var error = cotizacion.ValidarFinalizadaAntesDe(accion);

        error!.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        error.Mensaje.Should().StartWith($"Finaliza la cotización {cotizacion.Folio} antes de {accion}");
        error.Datos!["accion"].Should().Be("finalizar");
        error.Datos["metodo"].Should().Be("POST");
        error.Datos["endpoint"].Should().Be($"/web/cotizaciones/{cotizacion.Folio}/finalizar");
    }

    [Fact]
    public void FinalizadaElCierreNoBloquea()
    {
        Finalizada().ValidarFinalizadaAntesDe("generar el PDF").Should().BeNull();
    }

    [Fact]
    public void EntregadaAlClienteSinFinalizarElCierreNoExigeFinalizar()
    {
        // Las enviadas o con resultado sin finalizar ya las protege EntregadaAlCliente.
        var enviada = clsCotizacionMuestra.Costeada();
        enviada.RegistrarEnvio();
        var conResultado = clsCotizacionMuestra.Costeada(estado: EstadoCosteo.Entregada);
        conResultado.MarcarSeguimiento(false, "Precio", _ahora);

        enviada.ValidarFinalizadaAntesDe("enviarla al cliente").Should().BeNull();
        conResultado.ValidarFinalizadaAntesDe("enviarla al cliente").Should().BeNull();
        enviada.ValidarFinalizadaAntesDelResultado().Should().BeNull();
    }

    [Fact]
    public void LaCotizacionDelChatNoPasaPorLaFinalizacion()
    {
        var chat = clsCotizacionMuestra.Chat();

        chat.ValidarFinalizadaAntesDe("enviarla al cliente").Should().BeNull();
        chat.ValidarFinalizadaAntesDelResultado().Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoCosteo.Autocosteo)]
    [InlineData(EstadoCosteo.Entregada)]
    public void ConElCosteoListoElResultadoExigeFinalizar(EstadoCosteo estado)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(estado: estado);

        var error = cotizacion.ValidarFinalizadaAntesDelResultado();

        error!.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        error.Mensaje.Should().StartWith($"Finaliza la cotización {cotizacion.Folio} antes de marcar el resultado");
        Finalizada(estado).ValidarFinalizadaAntesDelResultado().Should().BeNull();
    }

    [Fact]
    public void ConConexosDeNexoPendientesDeLeerElCierreYElResultadoSiguenExigiendoFinalizar()
    {
        // Mientras no se pueda finalizar por un conexo sin leer, el PDF, el correo, los textos y el resultado esperan;
        // la regla del resultado no se apaga por no poder finalizar todavía.
        var cotizacion = clsCotizacionMuestra.Costeada();
        cotizacion.MarcarConexosPendientesDeLeer();

        cotizacion.Finalizar(_ana, _ahora).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        cotizacion.ValidarFinalizadaAntesDelResultado()!.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
        cotizacion.ValidarFinalizadaAntesDe("generar el PDF")!.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizada);
    }

    [Fact]
    public void AntesDelCosteoElResultadoNoExigeFinalizar()
    {
        // La perdida solo en Cotifly y la ganada pendiente de sincronizar en pendiente_costeo no exigen finalizar.
        var pendiente = clsCotizacionMuestra.Costeada(flete: null);
        pendiente.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        var conError = clsCotizacionMuestra.Formulario();
        conError.RegistrarErrorEnvioCosteo("Nexo no respondió");

        pendiente.ValidarFinalizadaAntesDelResultado().Should().BeNull();
        conError.ValidarFinalizadaAntesDelResultado().Should().BeNull();
    }

    [Fact]
    public void DescartadaYCosteoVencidoSiguenMarcandoseSoloEnCotifly()
    {
        // Decisión de Sara: en descartada y costeo_vencido la perdida se marca por /seguimiento (ResultadoSoloLocal), sin finalizar.
        var vencida = clsCotizacionMuestra.Costeada(flete: null);
        vencida.VencerPollingCosteo("Nexo no costeó en 72 h.");

        vencida.ResultadoSoloLocal.Should().BeTrue();
        vencida.ValidarFinalizadaAntesDelResultado().Should().BeNull();
        vencida.AdmiteFinalizar.Should().BeFalse();
    }
}
