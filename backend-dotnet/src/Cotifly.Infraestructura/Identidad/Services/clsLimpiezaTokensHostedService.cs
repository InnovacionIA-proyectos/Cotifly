using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// Purga periódica (cada hora; la primera, al minuto de arrancar) de los <c>jti</c> revocados ya vencidos y de los refresh
/// vencidos hace más de <see cref="RetencionRefreshVencidos"/> (ADR-009). Cada ronda abre su propio ámbito de DI. Un fallo
/// (base caída, sin cadena de conexión) queda como warning y se reintenta en la ronda siguiente: nunca detiene la API.
/// </summary>
public sealed class clsLimpiezaTokensHostedService(
    IServiceScopeFactory ambitos, IClsReloj reloj, TimeProvider tiempo, ILogger<clsLimpiezaTokensHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);
    public static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(1);

    /// <summary>Los refresh vencidos se conservan un día más (diagnóstico de reusos tardíos) antes de borrarlos.</summary>
    public static readonly TimeSpan RetencionRefreshVencidos = TimeSpan.FromDays(1);

    /// <summary>Una ronda de purga. Devuelve (jti borrados, refresh borrados) o <c>null</c> si falló.</summary>
    public async Task<(int Revocados, int Refresh)?> LimpiarAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ahora = reloj.AhoraUtc;
            await using var ambito = ambitos.CreateAsyncScope();
            var revocados = await ambito.ServiceProvider.GetRequiredService<IClsTokenRevocadoRepository>().PurgarVencidosAsync(ahora, cancellationToken);
            var refresh = await ambito.ServiceProvider.GetRequiredService<IClsTokenRefrescoRepository>()
                .PurgarVencidosAsync(ahora - RetencionRefreshVencidos, cancellationToken);
            if (revocados + refresh > 0)
            {
                logger.LogInformation("Purga de tokens: {Revocados} jti revocados y {Refresh} refresh vencidos.", revocados, refresh);
            }

            return (revocados, refresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("No se pudo purgar los tokens vencidos ({Tipo}): {Mensaje}", ex.GetType().Name, ex.Message);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(EsperaInicial, tiempo, stoppingToken);
            using var temporizador = new PeriodicTimer(Intervalo, tiempo);
            do
            {
                await LimpiarAsync(stoppingToken);
            }
            while (await temporizador.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado de la API.
        }
    }
}
