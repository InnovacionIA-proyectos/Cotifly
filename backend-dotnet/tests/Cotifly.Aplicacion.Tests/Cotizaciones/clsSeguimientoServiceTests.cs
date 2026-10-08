using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// Seguimiento ganada/perdida como caso de uso (deuda de ADR-008: antes vivía en el repositorio). Paridad con
/// <c>CotizacionRepo.marcar_seguimiento</c> (None si no es suya; guarda ganada, motivo y fecha).
/// </summary>
public sealed class clsSeguimientoServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly clsRelojFalso _reloj = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsSeguimientoServiceTests()
    {
        _cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-S1", UsuarioId = _ana }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-S1", _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsSeguimientoService Servicio() => new(_cotizaciones, _unidadTrabajo, _reloj);

    [Fact]
    public async Task ElDuenoMarcaLaCotizacionConMotivoYFecha()
    {
        var resultado = await Servicio().MarcarAsync(_ana, "PCN-S1", false, "Precio alto", Ct);

        resultado.Valor.Should().BeSameAs(_cotizacion);
        _cotizacion.Ganada.Should().BeFalse();
        _cotizacion.MotivoRechazo.Should().Be("Precio alto");
        _cotizacion.MarcadoAt.Should().Be(_reloj.AhoraUtc);
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtroComercialNoPuedeMarcarla()
    {
        var resultado = await Servicio().MarcarAsync(Guid.NewGuid(), "PCN-S1", true, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _cotizacion.Ganada.Should().BeNull();
        await _unidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task UnaCotizacionDelChatConReferenciaEnCosteoIdSeSigueMarcandoAqui()
    {
        var chat = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-C1", UsuarioId = _ana, CosteoId = "TARIFA-REF-9" }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C1", _ana, Arg.Any<CancellationToken>()).Returns(chat);

        (await Servicio().MarcarAsync(_ana, "PCN-C1", true, null, Ct)).Valor.Ganada.Should().BeTrue("costeoId no dice que pasó por Nexo");
    }

    [Theory]
    [InlineData(EstadoCosteo.Descartada)]
    [InlineData(EstadoCosteo.CosteoVencido)]
    public async Task UnaDescartadaOVencidaSeMarcaSoloEnCotifly(EstadoCosteo estado)
    {
        var cotizacion = EnFlujo("PCN-D1", estado);

        (await Servicio().MarcarAsync(_ana, "PCN-D1", false, "Cliente desistió", Ct)).Valor.Ganada.Should().BeFalse();
    }

    private clsCotizacionSolicitud EnFlujo(string folio, EstadoCosteo estado)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc);
        cotizacion.RegistrarConsultaCosteo(estado, "COT-1", _reloj.AhoraUtc, null);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        return cotizacion;
    }

    [Fact]
    public async Task UnaCotizacionQuePasoPorNexoNoSeMarcaSoloEnCotifly()
    {
        var enNexo = EnFlujo("PCN-N1", EstadoCosteo.Entregada);

        var resultado = await Servicio().MarcarAsync(_ana, "PCN-N1", true, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.UsarResultadoNexo);
        enNexo.Ganada.Should().BeNull();
        await _unidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task UnMotivoDemasiadoLargoSeRechaza()
    {
        var resultado = await Servicio().MarcarAsync(_ana, "PCN-S1", false, new string('m', 2001), Ct);

        resultado.Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);
        _cotizacion.MarcadoAt.Should().BeNull();
    }
}
