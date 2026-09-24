using Cotifly.Infraestructura.Comun;

namespace Cotifly.Infraestructura.Tests.Comun;

public sealed class clsRelojSistemaTests
{
    [Fact]
    public void AhoraUtcDevuelveLaHoraDelTimeProvider()
    {
        var instante = new DateTimeOffset(2026, 9, 24, 15, 30, 0, TimeSpan.Zero);
        var reloj = new clsRelojSistema(new clsTimeProviderFijo(instante));

        reloj.AhoraUtc.Should().Be(instante);
    }

    [Fact]
    public void AhoraUtcSiempreEstaEnUtc()
    {
        var instanteBogota = new DateTimeOffset(2026, 9, 24, 10, 30, 0, TimeSpan.FromHours(-5));
        var reloj = new clsRelojSistema(new clsTimeProviderFijo(instanteBogota));

        reloj.AhoraUtc.Offset.Should().Be(TimeSpan.Zero);
        reloj.AhoraUtc.Should().Be(instanteBogota);
    }

    [Fact]
    public void TimeProviderNuloLanzaArgumentNull()
    {
        var crear = () => new clsRelojSistema(null!);

        crear.Should().Throw<ArgumentNullException>();
    }

    private sealed class clsTimeProviderFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }
}
