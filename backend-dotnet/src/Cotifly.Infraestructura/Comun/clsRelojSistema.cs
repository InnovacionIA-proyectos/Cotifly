using Cotifly.Aplicacion.Comun.Interfaces;

namespace Cotifly.Infraestructura.Comun;

/// <summary>Adaptador de <see cref="IClsReloj"/> sobre <see cref="TimeProvider"/>.</summary>
public sealed class clsRelojSistema : IClsReloj
{
    private readonly TimeProvider _timeProvider;

    public clsRelojSistema(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public DateTimeOffset AhoraUtc => _timeProvider.GetUtcNow().ToUniversalTime();
}
