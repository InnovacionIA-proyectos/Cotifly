using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-036.2 (#206): finalizar la cotización en el agregado (<see cref="clsCotizacionSolicitud.Finalizar"/>). Solo con el costeo de Nexo
/// listo (<c>autocosteo</c> o <c>entregada</c>); re-finalizar no cambia nada; el estado de costeo sigue siendo el de Nexo.
/// </summary>
public sealed class clsCotizacionFinalizadaTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid _ana = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(EstadoCosteo.Autocosteo)]
    [InlineData(EstadoCosteo.Entregada)]
    public void ConElCosteoListoSeFinalizaConLaFechaYElComercial(EstadoCosteo estado)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(estado: estado);
        var precio = cotizacion.PrecioFinal;

        var resultado = cotizacion.Finalizar(_ana, _ahora);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().BeTrue();
        cotizacion.Finalizada.Should().BeTrue();
        cotizacion.FinalizadaAt.Should().Be(_ahora);
        cotizacion.FinalizadaPor.Should().Be(_ana);
        cotizacion.EstadoCosteo.Should().Be(estado, "la finalización no toca el estado de costeo de Nexo");
        cotizacion.PrecioFinal.Should().Be(precio);
    }

    [Fact]
    public void ReFinalizarNoCambiaLaFechaNiElComercial()
    {
        var cotizacion = clsCotizacionMuestra.Costeada();
        cotizacion.Finalizar(_ana, _ahora);

        var otra = cotizacion.Finalizar(Guid.NewGuid(), _ahora.AddHours(1));

        otra.EsExitoso.Should().BeTrue();
        otra.Valor.Should().BeFalse("ya estaba finalizada: idempotente");
        cotizacion.FinalizadaAt.Should().Be(_ahora);
        cotizacion.FinalizadaPor.Should().Be(_ana);
    }

    [Theory]
    [InlineData(EstadoCosteo.PendienteCosteo)]
    [InlineData(EstadoCosteo.Descartada)]
    [InlineData(EstadoCosteo.CosteoVencido)]
    public void SinElCosteoListoNoSeFinaliza(EstadoCosteo estado)
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        if (estado == EstadoCosteo.CosteoVencido)
        {
            cotizacion.VencerPollingCosteo("Nexo no costeó en 72 h.");
        }
        else if (estado == EstadoCosteo.Descartada)
        {
            foreach (var camion in cotizacion.Vehiculos)
            {
                camion.RegistrarCosteo(EstadoCosteo.Descartada, null, null);
            }

            cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Descartada, null, null);
        }

        cotizacion.EstadoCosteo.Should().Be(estado);

        var resultado = cotizacion.Finalizar(_ana, _ahora);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!["estado_costeo"].Should().Be(clsCodigoEnum.ACodigo(cotizacion.EstadoCosteo!.Value));
        cotizacion.Finalizada.Should().BeFalse();
        cotizacion.FinalizadaPor.Should().BeNull();
    }

    [Fact]
    public void EnPendienteDeEnvioYConErrorDeEnvioTampoco()
    {
        var pendiente = clsCotizacionMuestra.Formulario();
        pendiente.Finalizar(_ana, _ahora).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);

        var conError = clsCotizacionMuestra.Formulario();
        conError.RegistrarErrorEnvioCosteo("Nexo no respondió");
        conError.Finalizar(_ana, _ahora).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
    }

    [Fact]
    public void UnaEntregadaQueCotiflyTodaviaNoLeyoNoSeFinalizaSinPrecio()
    {
        // HU-013.14: entregada en Nexo sin el precio leído (EntregadaSinPrecio). Finalizarla congelaría un precio vacío (#208).
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        cotizacion.RegistrarEntregaImplicita("COT-1");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.PrecioFinal.Should().BeNull();

        var resultado = cotizacion.Finalizar(_ana, _ahora);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!["estado_costeo"].Should().Be("entregada");
        cotizacion.Finalizada.Should().BeFalse();
    }

    [Fact]
    public void ConConexosDeNexoPendientesDeLeerNoSeFinalizaHastaQueElPollingLosTraiga()
    {
        // Validación HU-036: el conexo ya está en Nexo pero no en el precio de Cotifly; con el precio congelado (#208) nunca entraría.
        var cotizacion = clsCotizacionMuestra.Costeada();
        cotizacion.MarcarConexosPendientesDeLeer();

        var resultado = cotizacion.Finalizar(_ana, _ahora);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Mensaje.Should().Be(clsCotizacionSolicitud.MotivoNoFinalizableConexosPendientes);
        resultado.Error.Datos!["motivo"].Should().Be(clsCotizacionSolicitud.CodigoConexosPendientesDeLeer);
        resultado.Error.Datos!["estado_costeo"].Should().Be("autocosteo");
        cotizacion.Finalizada.Should().BeFalse();

        cotizacion.QuitarMarcaConexosPendientes();
        cotizacion.Finalizar(_ana, _ahora).Valor.Should().BeTrue("leídos los conexos, se finaliza");
    }

    [Fact]
    public void LaCotizacionDelChatNoSeFinalizaYElDetalleLoDice()
    {
        var chat = clsCotizacionMuestra.Chat();

        var resultado = chat.Finalizar(_ana, _ahora);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Mensaje.Should().Contain("sin costeo en Nexo");
        resultado.Error.Datos!["estado_costeo"].Should().BeNull();
    }

    [Fact]
    public void FinalizarSinComercialEsUnErrorDeProgramacion()
    {
        var cotizacion = clsCotizacionMuestra.Costeada();

        var accion = () => cotizacion.Finalizar(Guid.Empty, _ahora);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LaFinalizadaSigueAvanzandoConNexoYNoPierdeLaFinalizacion()
    {
        var cotizacion = clsCotizacionMuestra.Costeada();
        cotizacion.Finalizar(_ana, _ahora);

        cotizacion.RegistrarEntregaImplicita("COT-1");

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.Finalizada.Should().BeTrue();
    }

    [Fact]
    public void LaVersionNuevaNaceSinFinalizar()
    {
        var cotizacion = clsCotizacionMuestra.Costeada();
        cotizacion.Finalizar(_ana, _ahora);

        var version = cotizacion.CrearVersion(clsFolio.Desde(cotizacion.Folio).Valor.ConVersion(2).Valor, 0m);

        version.Valor.Finalizada.Should().BeFalse();
        version.Valor.FinalizadaAt.Should().BeNull();
    }
}
