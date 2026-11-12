using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Margen;

/// <summary>
/// <see cref="clsSapMargenAdminService"/>. Solo gerencia; sin configuración, <c>SAP_MARGEN_NO_CONFIGURADO</c>; una corrida en
/// curso, <c>SAP_MARGEN_SINCRONIZACION_EN_CURSO</c>; la corrida forzada se audita con su resultado y sin credenciales.
/// </summary>
public sealed class clsSapMargenAdminServiceTests
{
    private readonly IClsSincronizacionMargenSap _sincronizacion = Substitute.For<IClsSincronizacionMargenSap>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();

    public clsSapMargenAdminServiceTests()
    {
        _sincronizacion.Habilitada.Returns(true);
        _sincronizacion.Configurada.Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SoloLaGerenciaVeElEstadoYSincroniza()
    {
        var servicio = Servicio(RolInterno.Comercial);

        var estado = await servicio.EstadoAsync(Ct);
        var sincronizar = await servicio.SincronizarAsync(Ct);

        estado.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
        sincronizar.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
        await _sincronizacion.DidNotReceiveWithAnyArgs().SincronizarAsync(default, Ct);
    }

    [Fact]
    public async Task HabilitadaSinConfiguracionValidaRespondeSapMargenNoConfiguradoConElMotivo()
    {
        _sincronizacion.Configurada.Returns(false);
        _sincronizacion.ErrorConfiguracion.Returns("'SapTm:SapClient' debe tener exactamente 3 dígitos (ej. 100) o quedar vacío.");

        var estado = await Servicio(RolInterno.Director).EstadoAsync(Ct);

        estado.Error.Codigo.Should().Be(clsCodigosError.SapMargenNoConfigurado);
        estado.Error.Detalle.Should().Contain("'SapTm:SapClient'");
    }

    [Fact]
    public async Task DeshabilitadaElEstadoRespondeYLaCorridaForzadaDa503SinLlamarALaSincronizacion()
    {
        _sincronizacion.Habilitada.Returns(false);
        _sincronizacion.Configurada.Returns(false);
        var estadoCopia = new clsEstadoSincronizacionMargenSap(null, null, 0, DateTimeOffset.UtcNow, 1440, 60, false, false, false);
        _sincronizacion.EstadoAsync(Arg.Any<CancellationToken>()).Returns(estadoCopia);
        var servicio = Servicio(RolInterno.GerenteComercial);

        var estado = await servicio.EstadoAsync(Ct);
        var sincronizar = await servicio.SincronizarAsync(Ct);

        estado.Valor.Should().Be(estadoCopia);
        sincronizar.Error.Codigo.Should().Be(clsCodigosError.SapMargenNoConfigurado);
        sincronizar.Error.Mensaje.Should().Contain("deshabilitada");
        await _sincronizacion.DidNotReceiveWithAnyArgs().SincronizarAsync(default, Ct);
    }

    [Fact]
    public async Task UnaCorridaEnCursoRespondeSapMargenSincronizacionEnCurso()
    {
        _sincronizacion.SincronizarAsync(true, Arg.Any<CancellationToken>())
            .Returns(clsResultadoSincronizacionMargenSap.SinCorrida(clsResultadoSincronizacionMargenSap.EstadoEnCurso));

        var r = await Servicio(RolInterno.GerenteComercial).SincronizarAsync(Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.SapMargenSincronizacionEnCurso);
    }

    [Fact]
    public async Task LaCorridaForzadaDevuelveSuResultadoAunqueSeaErrorYQuedaAuditada()
    {
        var corrida = new clsCorridaSincronizacionMargenSap("error", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, "SAP TM respondió HTTP 503.");
        _sincronizacion.SincronizarAsync(true, Arg.Any<CancellationToken>()).Returns(new clsResultadoSincronizacionMargenSap("error", 0, 15, corrida));

        var r = await Servicio(RolInterno.GerenteComercial).SincronizarAsync(Ct);

        r.Valor.Estado.Should().Be("error");
        r.Valor.Corrida.Should().Be(corrida);
        await _auditoria.Received(1).RegistrarAsync(clsSapMargenAdminService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false,
            Arg.Any<int>(), null);
    }

    [Fact]
    public async Task ConLaBaseCaidaRespondeSapMargenNoDisponibleNoUnErrorInterno()
    {
        _sincronizacion.EstadoAsync(Arg.Any<CancellationToken>()).Returns<clsEstadoSincronizacionMargenSap>(_ => throw new clsSincronizacionMargenSapNoDisponibleException());
        _sincronizacion.SincronizarAsync(true, Arg.Any<CancellationToken>())
            .Returns<clsResultadoSincronizacionMargenSap>(_ => throw new clsSincronizacionMargenSapNoDisponibleException());
        var servicio = Servicio(RolInterno.Director);

        var estado = await servicio.EstadoAsync(Ct);
        var sincronizar = await servicio.SincronizarAsync(Ct);

        estado.Error.Codigo.Should().Be(clsCodigosError.SapMargenNoDisponible);
        sincronizar.Error.Codigo.Should().Be(clsCodigosError.SapMargenNoDisponible);
    }

    private clsSapMargenAdminService Servicio(RolInterno? rol) => new(new clsUsuario(rol), _sincronizacion, _auditoria);

    private sealed class clsUsuario(RolInterno? rol) : IClsContextoUsuario
    {
        public bool EstaAutenticado => true;

        public Guid? UsuarioId { get; } = rol is null ? null : Guid.NewGuid();

        public string? NombreUsuario => "usuario.prueba";

        public RolInterno? Rol { get; } = rol;
    }
}
