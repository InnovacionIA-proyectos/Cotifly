using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>Revocación por jti en memoria con foto de <c>TokensRevocados</c> recargada cada 60 s y fail-closed.</summary>
public sealed class clsCacheTokensRevocadosTests : IDisposable
{
    private readonly clsRelojFalso _reloj = new();
    private readonly IClsTokenRevocadoRepository _repositorio = Substitute.For<IClsTokenRevocadoRepository>();
    private readonly IServiceScopeFactory _ambitos = Substitute.For<IServiceScopeFactory>();
    private readonly clsCacheTokensRevocados _cache;

    public clsCacheTokensRevocadosTests()
    {
        _repositorio.ListarVigentesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<clsJtiRevocado>());
        var ambito = Substitute.For<IServiceScope>();
        var servicios = Substitute.For<IServiceProvider>();
        servicios.GetService(typeof(IClsTokenRevocadoRepository)).Returns(_repositorio);
        ambito.ServiceProvider.Returns(servicios);
        _ambitos.CreateScope().Returns(ambito);
        _cache = new clsCacheTokensRevocados(_ambitos, _reloj, new clsLoggerFalso<clsCacheTokensRevocados>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnJtiRevocadoEnEsteProcesoSeRechazaAlInstanteHastaQueVence()
    {
        _cache.RegistrarLocal("jti-1", _reloj.AhoraUtc.AddMinutes(10));

        (await _cache.EstaRevocadoAsync("jti-1", Ct)).Should().BeTrue();
        (await _cache.EstaRevocadoAsync("jti-2", Ct)).Should().BeFalse();
        _reloj.Avanzar(TimeSpan.FromMinutes(11));
        (await _cache.EstaRevocadoAsync("jti-1", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task LosRevocadosDeOtrasReplicasLleganConLaFotoQueSeRecargaCadaSesentaSegundos()
    {
        (await _cache.EstaRevocadoAsync("jti-otra", Ct)).Should().BeFalse();
        _repositorio.ListarVigentesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new clsJtiRevocado("jti-otra", _reloj.AhoraUtc.AddMinutes(30)) });

        _reloj.Avanzar(TimeSpan.FromSeconds(30));
        (await _cache.EstaRevocadoAsync("jti-otra", Ct)).Should().BeFalse("la foto todavía no se recarga");
        _reloj.Avanzar(TimeSpan.FromSeconds(31));
        (await _cache.EstaRevocadoAsync("jti-otra", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task SiLaRecargaFallaSeUsaLaFotoAnteriorHastaCincoMinutosYLuegoFallaCerrado()
    {
        (await _cache.EstaRevocadoAsync("jti-1", Ct)).Should().BeFalse();
        _repositorio.ListarVigentesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        _reloj.Avanzar(TimeSpan.FromMinutes(2));
        (await _cache.EstaRevocadoAsync("jti-1", Ct)).Should().BeFalse("la foto anterior tiene menos de 5 minutos");
        _reloj.Avanzar(TimeSpan.FromMinutes(4));
        (await _cache.EstaRevocadoAsync("jti-1", Ct)).Should().BeTrue("sin poder recargar por más de 5 minutos se falla cerrado");
    }

    [Fact]
    public async Task SinNingunaFotoCargadaTodoJtiSeConsideraRevocado()
    {
        _repositorio.ListarVigentesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        (await _cache.EstaRevocadoAsync("cualquiera", Ct)).Should().BeTrue();
    }

    public void Dispose() => _cache.Dispose();
}
