using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>Refresh con rotación atómica, detección de reuso y nueva verificación doble (ADR-009).</summary>
public sealed class clsRefreshTokenServiceTests
{
    private const string RefreshPlano = "refresh-opaco-de-prueba";
    private readonly clsEscenarioIdentidad _e = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SinRefreshEsSesionExpiradaSinTocarLaBase(string? refresh)
    {
        var resultado = await _e.Refresh().RenovarAsync(refresh, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        resultado.Error.Mensaje.Should().Be("Inicia sesión de nuevo.");
        await _e.Tokens.DidNotReceiveWithAnyArgs().ObtenerPorHashAsync(default!, Ct);
    }

    [Fact]
    public async Task UnRefreshDesconocidoEsSesionExpirada()
    {
        var resultado = await _e.Refresh().RenovarAsync("no-existe", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RefreshDesconocido);
    }

    [Fact]
    public async Task UnRefreshVigenteRotaHeredandoLaFamiliaYElVencimientoYEmiteUnParNuevo()
    {
        var (usuario, token) = SesionVigente();
        _e.Reloj.Avanzar(TimeSpan.FromDays(2));
        clsTokenRefresco? nuevo = null;
        _e.Tokens.RotarAsync(token.Id, Arg.Do<clsTokenRefresco>(t => nuevo = t), _e.Reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(true);

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        var sesion = resultado.Valor;
        sesion.Refresh.Should().NotBe(RefreshPlano);
        sesion.Usuario.Id.Should().Be(usuario.Id);
        sesion.Usuario.Rol.Should().Be("comercial");
        nuevo!.FamiliaId.Should().Be(token.FamiliaId);
        nuevo.ExpiraAt.Should().Be(token.ExpiraAt, "la rotación no extiende la sesión de 30 días");
        nuevo.TokenHash.Should().Be(clsTokenOpaco.Hash(sesion.Refresh));
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.Renovado);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaRefresh).Should().Be((true, "auth/APEREZ"));
    }

    [Fact]
    public async Task SiOtraRenovacionGanoLaRotacionEsSesionExpiradaSinRevocarNada()
    {
        var (_, token) = SesionVigente();
        _e.Tokens.RotarAsync(token.Id, Arg.Any<clsTokenRefresco>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RotacionConcurrente);
        await _e.Tokens.DidNotReceiveWithAnyArgs().RevocarActivosDelUsuarioAsync(default, default, Ct);
    }

    [Fact]
    public async Task ReutilizarUnRefreshYaRotadoRevocaTodosLosRefreshDelUsuario()
    {
        var (usuario, token) = SesionVigente();
        token.MarcarReemplazado(Guid.NewGuid(), _e.Reloj.AhoraUtc);
        _e.Reloj.Avanzar(TimeSpan.FromMinutes(1));

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        await _e.Tokens.Received(1).RevocarActivosDelUsuarioAsync(usuario.Id, _e.Reloj.AhoraUtc, Arg.Any<CancellationToken>());
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RefreshReutilizado);
        _e.LoggerRefresh.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("Reuso"));
    }

    [Fact]
    public async Task UnRefreshRevocadoPorLogoutTambienCuentaComoReuso()
    {
        var (usuario, token) = SesionVigente();
        token.Revocar(_e.Reloj.AhoraUtc);

        await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        await _e.Tokens.Received(1).RevocarActivosDelUsuarioAsync(usuario.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnRefreshRotadoHacePocosSegundosEsRenovacionConcurrenteNoRobo()
    {
        var (_, token) = SesionVigente();
        token.MarcarReemplazado(Guid.NewGuid(), _e.Reloj.AhoraUtc);
        _e.Reloj.Avanzar(TimeSpan.FromSeconds(3));

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        await _e.Tokens.DidNotReceiveWithAnyArgs().RevocarActivosDelUsuarioAsync(default, default, Ct);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RotacionConcurrente);
    }

    [Fact]
    public async Task UnRefreshVencidoEsSesionExpirada()
    {
        SesionVigente();
        _e.Reloj.Avanzar(TimeSpan.FromDays(30));

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RefreshVencido);
        await _e.Tokens.DidNotReceiveWithAnyArgs().RotarAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task SiElRolExternoCambioElRefreshSeRechaza()
    {
        SesionVigente();
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoGerente);

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolExternoCambio);
        await _e.Tokens.DidNotReceiveWithAnyArgs().RotarAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task ConElAlmacenCaidoElRefreshFallaCerrado()
    {
        SesionVigente();
        _e.Almacen.BuscarUsuarioAsync("APEREZ", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.AlmacenNoDisponible);
    }

    [Fact]
    public async Task SiElUsuarioYaNoExisteElRefreshSeRechaza()
    {
        var token = clsTokenRefresco.Emitir(Guid.NewGuid(), clsTokenOpaco.Hash(RefreshPlano), _e.Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor;
        _e.Tokens.ObtenerPorHashAsync(clsTokenOpaco.Hash(RefreshPlano), Arg.Any<CancellationToken>()).Returns(token);

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaRefresh).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.UsuarioNoEncontrado);
    }

    [Fact]
    public async Task UnFalloInesperadoResponde401()
    {
        _e.Tokens.ObtenerPorHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var resultado = await _e.Refresh().RenovarAsync(RefreshPlano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.LoggerRefresh.Registros.Should().Contain(r => r.Nivel == LogLevel.Error);
    }

    /// <summary>APEREZ (ID externo 101, comercial) con un refresh vigente de 30 días emitido ahora.</summary>
    private (clsUsuarioWeb Usuario, clsTokenRefresco Token) SesionVigente()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        var token = clsTokenRefresco.Emitir(usuario.Id, clsTokenOpaco.Hash(RefreshPlano), _e.Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor;
        _e.Tokens.ObtenerPorHashAsync(clsTokenOpaco.Hash(RefreshPlano), Arg.Any<CancellationToken>()).Returns(token);
        return (usuario, token);
    }
}
