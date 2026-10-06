using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>Logout: revoca la familia del refresh y el jti del access; nunca falla hacia afuera (ADR-009).</summary>
public sealed class clsLogoutServiceTests
{
    private readonly clsEscenarioIdentidad _e = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConRefreshYAccessRevocaLaFamiliaYRegistraElJtiEnMemoriaYEnLaBase()
    {
        var usuario = Guid.NewGuid();
        var token = clsTokenRefresco.Emitir(usuario, clsTokenOpaco.Hash("r1"), _e.Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor;
        _e.Tokens.ObtenerPorHashAsync(clsTokenOpaco.Hash("r1"), Arg.Any<CancellationToken>()).Returns(token);
        var expira = _e.Reloj.AhoraUtc.AddMinutes(45);
        clsTokenRevocado? revocado = null;
        _e.Revocados.When(r => r.Agregar(Arg.Any<clsTokenRevocado>())).Do(c => revocado = c.Arg<clsTokenRevocado>());

        await _e.Logout().CerrarSesionAsync(new clsSolicitudLogout("r1", "jti-1", expira, usuario), Ct);

        await _e.Tokens.Received(1).RevocarFamiliaAsync(token.FamiliaId, _e.Reloj.AhoraUtc, Arg.Any<CancellationToken>());
        _e.Revocacion.Received(1).RegistrarLocal("jti-1", expira);
        revocado!.Jti.Should().Be("jti-1");
        revocado.ExpiraAt.Should().Be(expira);
        revocado.UsuarioId.Should().Be(usuario);
        await _e.UnidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogout).Exito.Should().BeTrue();
    }

    [Fact]
    public async Task SinTokensNoHaceNadaYTerminaSinError()
    {
        await _e.Logout().CerrarSesionAsync(new clsSolicitudLogout(null), Ct);

        await _e.Tokens.DidNotReceiveWithAnyArgs().ObtenerPorHashAsync(default!, Ct);
        _e.Revocados.DidNotReceiveWithAnyArgs().Agregar(default!);
        _e.Revocacion.DidNotReceiveWithAnyArgs().RegistrarLocal(default!, default);
    }

    [Fact]
    public async Task UnRefreshDesconocidoNoRevocaNada()
    {
        await _e.Logout().CerrarSesionAsync(new clsSolicitudLogout("desconocido"), Ct);

        await _e.Tokens.DidNotReceiveWithAnyArgs().RevocarFamiliaAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnLogoutRepetidoConElMismoAccessNoFalla()
    {
        _e.UnidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(
            new clsViolacionUnicidadException("pk_tokensRevocados", new clsErrorDominio("REGISTRO_DUPLICADO", "Ya revocado."), null));

        var cerrar = () => _e.Logout().CerrarSesionAsync(new clsSolicitudLogout(null, "jti-1", _e.Reloj.AhoraUtc.AddMinutes(10)), Ct);

        await cerrar.Should().NotThrowAsync();
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogout).Exito.Should().BeTrue();
    }

    [Fact]
    public async Task ConLaBaseCaidaElJtiQuedaRevocadoEnEsteProcesoYNoFalla()
    {
        _e.Tokens.ObtenerPorHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var expira = _e.Reloj.AhoraUtc.AddMinutes(10);

        var cerrar = () => _e.Logout().CerrarSesionAsync(new clsSolicitudLogout("r1", "jti-1", expira), Ct);

        await cerrar.Should().NotThrowAsync();
        _e.Revocacion.Received(1).RegistrarLocal("jti-1", expira);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogout).Exito.Should().BeFalse();
    }
}
