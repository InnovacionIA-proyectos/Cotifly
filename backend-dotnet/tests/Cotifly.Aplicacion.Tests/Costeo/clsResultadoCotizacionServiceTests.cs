using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Resultado ganada/perdida en Nexo (HU-005.14, ADR-015 §6): dos escrituras, la local solo con 200 de Nexo; los 409 de Nexo y su
/// caída no cambian nada local; <c>comentario</c> solo se reenvía.
/// </summary>
public sealed class clsResultadoCotizacionServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacion _cotizacion;

    public clsResultadoCotizacionServiceTests()
    {
        _cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-004", UsuarioId = _ana }).Valor;
        _cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        _cotizacion.RegistrarEnvioCosteo("SOL-1", false, _reloj.AhoraUtc, _reloj.AhoraUtc);
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "COT-1", _reloj.AhoraUtc, null);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-1", "Entregada", "Ganada", [], []));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConElOkDeNexoSeGuardaGanadaYElMotivoPeroNoElComentario()
    {
        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", "Mejor precio", "Nota interna", Ct);

        resultado.Valor.Ganada.Should().BeTrue();
        _cotizacion.MotivoRechazo.Should().Be("Mejor precio");
        _cotizacion.MarcadoAt.Should().Be(_reloj.AhoraUtc);
        await _nexo.Received(1).MarcarResultadoAsync(_ana, "COT-1",
            Arg.Is<clsResultadoNexo>(r => r.Ganada && r.Motivo == "Mejor precio" && r.Comentario == "Nota interna"), Arg.Any<CancellationToken>());
        await _unidad.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(clsCodigosError.ResultadoYaDefinido)]
    [InlineData(clsCodigosError.CotizacionNoEntregada)]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado)]
    public async Task SiNexoNoRespondeOkNoSeCambiaNadaLocal(string codigo)
    {
        _nexo.MarcarResultadoAsync(_ana, "COT-1", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "Nexo dijo que no"));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "perdida", "Precio alto", null, Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        _cotizacion.Ganada.Should().BeNull();
        _cotizacion.MotivoRechazo.Should().BeNull();
        _cotizacion.MarcadoAt.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
        await _auditoria.Received(1).RegistrarAsync(clsResultadoCotizacionService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task LaEntregaImplicitaDejaLaCotizacionEntregadaSinTratarloComoError()
    {
        _cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _reloj.AhoraUtc, _reloj.AhoraUtc.AddMinutes(1));

        var resultado = await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", null, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        _cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public async Task SinIdDeLaCotizacionDeNexoLoRecuperaYLoGuarda()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-005", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarEnvioCosteo("SOL-2", true, _reloj.AhoraUtc, _reloj.AhoraUtc);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _nexo.ObtenerCotizacionAsync(_ana, "SOL-2", Arg.Any<CancellationToken>()).Returns(new clsCotizacionNexo("COT-2", "Entregada", "SinDefinir", [], []));
        _nexo.MarcarResultadoAsync(_ana, "COT-2", Arg.Any<clsResultadoNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsCotizacionNexo("COT-2", "Entregada", "Perdida", [], []));

        var resultado = await Servicio().MarcarAsync(_ana, cotizacion.Folio, "perdida", null, null, Ct);

        resultado.Valor.Ganada.Should().BeFalse();
        cotizacion.CotizacionNexoId.Should().Be("COT-2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("empate")]
    public async Task UnResultadoInvalidoEs400SinLlamarANexo(string? resultado)
    {
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, resultado, null, null, Ct)).Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);
        (await Servicio().MarcarAsync(_ana, _cotizacion.Folio, "ganada", new string('m', 101), null, Ct)).Error.Codigo.Should().Be(clsSeguimientoService.CodigoSeguimientoInvalido);

        await _nexo.DidNotReceiveWithAnyArgs().MarcarResultadoAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task UnaAjenaEsNoEncontradaYUnaSinNexoEsCosteoNoListo()
    {
        (await Servicio().MarcarAsync(Guid.NewGuid(), _cotizacion.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);

        var chat = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-006", UsuarioId = _ana }).Valor;
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(chat.Folio, _ana, Arg.Any<CancellationToken>()).Returns(chat);
        (await Servicio().MarcarAsync(_ana, chat.Folio, "ganada", null, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }

    private clsResultadoCotizacionService Servicio() => new(_cotizaciones, _nexo, _unidad, _auditoria, _reloj);
}
