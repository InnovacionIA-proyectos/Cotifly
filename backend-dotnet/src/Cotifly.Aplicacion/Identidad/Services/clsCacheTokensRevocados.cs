using System.Collections.Concurrent;
using System.Collections.Frozen;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>
/// Implementación de <see cref="IClsRevocacionAccesos"/> (singleton): los revocados en este proceso van a un diccionario
/// local (efecto inmediato) y los de todas las réplicas llegan con una foto de <c>TokensRevocados</c> que se recarga
/// cada <see cref="IntervaloRecarga"/> en un ámbito de DI propio. La tabla solo guarda los <c>jti</c> de los logouts de
/// la última hora (vencimiento del access), así que la foto es pequeña.
/// </summary>
/// <remarks>
/// Fail-closed: si la recarga falla se sigue usando la foto anterior durante <see cref="ToleranciaSinRecarga"/>; si no hay
/// foto o es más vieja que eso, todo <c>jti</c> se considera revocado (401 en los endpoints protegidos).
/// </remarks>
public sealed class clsCacheTokensRevocados(
    IServiceScopeFactory ambitos, IClsReloj reloj, ILogger<clsCacheTokensRevocados> logger) : IClsRevocacionAccesos, IDisposable
{
    public static readonly TimeSpan IntervaloRecarga = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ToleranciaSinRecarga = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _locales = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _candado = new(1, 1);
    private clsFoto? _foto;

    public async Task<bool> EstaRevocadoAsync(string jti, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jti);
        var ahora = reloj.AhoraUtc;
        if (_locales.TryGetValue(jti, out var expiraLocal) && expiraLocal > ahora)
        {
            return true;
        }

        var foto = await ObtenerFotoAsync(ahora, cancellationToken);
        if (foto is null)
        {
            return true;
        }

        return foto.Jtis.TryGetValue(jti, out var expira) && expira > ahora;
    }

    public void RegistrarLocal(string jti, DateTimeOffset expiraAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);
        _locales[jti] = expiraAt;
    }

    public void Dispose() => _candado.Dispose();

    private async Task<clsFoto?> ObtenerFotoAsync(DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        var foto = Volatile.Read(ref _foto);
        if (foto is not null && ahora - foto.CargadaAt < IntervaloRecarga)
        {
            return foto;
        }

        await _candado.WaitAsync(cancellationToken);
        try
        {
            foto = Volatile.Read(ref _foto);
            if (foto is not null && ahora - foto.CargadaAt < IntervaloRecarga)
            {
                return foto;
            }

            try
            {
                await using var ambito = ambitos.CreateAsyncScope();
                var vigentes = await ambito.ServiceProvider.GetRequiredService<IClsTokenRevocadoRepository>()
                    .ListarVigentesAsync(ahora, cancellationToken);
                var nueva = new clsFoto(vigentes.ToFrozenDictionary(v => v.Jti, v => v.ExpiraAt, StringComparer.Ordinal), ahora);
                Volatile.Write(ref _foto, nueva);
                foreach (var (jti, expira) in _locales)
                {
                    if (expira <= ahora)
                    {
                        _locales.TryRemove(jti, out _);
                    }
                }

                return nueva;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("No se pudo recargar la lista de access tokens revocados ({Tipo}): {Mensaje}", ex.GetType().Name, ex.Message);
                return foto is not null && ahora - foto.CargadaAt < ToleranciaSinRecarga ? foto : null;
            }
        }
        finally
        {
            _candado.Release();
        }
    }

    private sealed record clsFoto(FrozenDictionary<string, DateTimeOffset> Jtis, DateTimeOffset CargadaAt);
}
