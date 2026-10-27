using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-036.2 (#206): el caso de uso de <c>POST .../finalizar</c>. Dueño (404), <c>If-Match</c> (409 <c>CONFLICTO_CONCURRENCIA</c>), regla
/// del dominio (409 <c>COTIZACION_NO_FINALIZABLE</c>), idempotencia (200 sin guardar ni subir la versión) y auditoría.
/// </summary>
public sealed class clsFinalizacionCotizacionServiceTests
{
    private const int Version = 4;
    private static readonly DateTimeOffset _ahora = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly IClsEnvioConexosNexoService _envioConexos = Substitute.For<IClsEnvioConexosNexoService>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsFinalizacionCotizacionServiceTests()
    {
        _cotizacion = clsCotizacionMuestra.ConVersionNegocio(clsCotizacionMuestra.Costeada(usuarioId: _ana), Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _reloj.AhoraUtc.Returns(_ahora);
        _envioConexos.EstadosAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), string>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsFinalizacionCotizacionService Servicio() => new(_cotizaciones, _unidad, _reloj, _auditoria, _envioConexos);

    [Fact]
    public async Task FinalizaConLaVersionVigenteSubeLaVersionGuardaYAudita()
    {
        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.FinalizadaAt.Should().Be(_ahora);
        resultado.Valor.FinalizadaPor.Should().Be(_ana);
        resultado.Valor.VersionNegocio.Should().Be(Version + 1);
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(clsFinalizacionCotizacionService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ReFinalizarDevuelveLaMismaSinGuardarNiSubirLaVersionNiAuditar()
    {
        _cotizacion.Finalizar(_ana, _ahora.AddHours(-1));

        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.FinalizadaAt.Should().Be(_ahora.AddHours(-1));
        resultado.Valor.VersionNegocio.Should().Be(Version);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public async Task ConOtraVersionEsConflictoAunqueYaEsteFinalizada()
    {
        var antes = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version - 1, Ct);
        antes.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _cotizacion.Finalizada.Should().BeFalse();

        _cotizacion.Finalizar(_ana, _ahora);
        var despues = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version - 1, Ct);
        despues.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task SiOtraEscrituraGanaElGuardadoEsConflicto()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _auditoria.Received(1).RegistrarAsync(clsFinalizacionCotizacionService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SinElCosteoListoEsNoFinalizableYNoGuarda()
    {
        var pendiente = clsCotizacionMuestra.Costeada(flete: null, usuarioId: _ana, folio: "PCN-AP-28092026-077");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(pendiente.Folio, _ana, Arg.Any<CancellationToken>()).Returns(pendiente);

        var resultado = await Servicio().FinalizarAsync(_ana, pendiente.Folio, 0, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!["estado_costeo"].Should().Be("pendiente_costeo");
        pendiente.Finalizada.Should().BeFalse();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsFinalizacionCotizacionService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    /// <summary>
    /// #228: un conexo de una ruta cotizable con <c>estado_nexo = pendiente</c> (todavía no está en la cotización de Nexo) impide finalizar:
    /// 409 <c>COTIZACION_NO_FINALIZABLE</c> con <c>detalle.motivo = conexos_pendientes_en_nexo</c>, sin guardar. Los enviados, inciertos o
    /// fallidos definitivos no lo impiden (ya no se esperan del polling).
    /// </summary>
    [Fact]
    public async Task ConUnConexoPendienteEnNexoEsNoFinalizableYNoGuarda()
    {
        var ruta = _cotizacion.PrimeraRuta!;
        _envioConexos.EstadosAsync(_cotizacion, Arg.Any<CancellationToken>()).Returns(new Dictionary<(Guid, string), string>
        {
            [(ruta.Id, "CNX-1")] = clsEnvioConexosNexoService.VisibleEnviado,
            [(ruta.Id, "CNX-2")] = clsEnvioConexosNexoService.VisiblePendiente,
            [(ruta.Id, "CNX-3")] = clsEnvioConexosNexoService.VisibleIncierto,
        });

        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!["motivo"].Should().Be(clsCotizacionSolicitud.CodigoConexosPendientesEnNexo);
        resultado.Error.Datos!["conexos_pendientes"].Should().Be(1);
        _cotizacion.Finalizada.Should().BeFalse();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsFinalizacionCotizacionService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ConLosConexosEnviadosInciertosOFallidosSiSeFinaliza()
    {
        var ruta = _cotizacion.PrimeraRuta!;
        _envioConexos.EstadosAsync(_cotizacion, Arg.Any<CancellationToken>()).Returns(new Dictionary<(Guid, string), string>
        {
            [(ruta.Id, "CNX-1")] = clsEnvioConexosNexoService.VisibleEnviado,
            [(ruta.Id, "CNX-2")] = clsEnvioConexosNexoService.VisibleIncierto,
            [(ruta.Id, "CNX-3")] = clsEnvioConexosNexoService.VisibleFallidoDefinitivo,
        });

        (await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task UnConexoPendienteDeUnaRutaQueNoEstaEnLaCotizacionNoCuenta()
    {
        // Una ruta rechazada por la Mesa (o un id que ya no es de la cotización) nunca tendrá su conexo en Nexo: no bloquea para siempre.
        _envioConexos.EstadosAsync(_cotizacion, Arg.Any<CancellationToken>()).Returns(new Dictionary<(Guid, string), string>
        {
            [(Guid.NewGuid(), "CNX-1")] = clsEnvioConexosNexoService.VisiblePendiente,
        });

        (await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task ReFinalizarNoConsultaLosConexos()
    {
        _cotizacion.Finalizar(_ana, _ahora.AddHours(-1));

        (await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();
        await _envioConexos.DidNotReceiveWithAnyArgs().EstadosAsync(default!, Ct);
    }

    [Fact]
    public async Task UnaAjenaOInexistenteEsNoEncontradaSinAuditar()
    {
        var ajena = await Servicio().FinalizarAsync(Guid.NewGuid(), _cotizacion.Folio, Version, Ct);
        var sinUsuario = await Servicio().FinalizarAsync(Guid.Empty, _cotizacion.Folio, Version, Ct);

        ajena.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        sinUsuario.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _cotizacion.Finalizada.Should().BeFalse();
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }
}
