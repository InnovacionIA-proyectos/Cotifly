using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Procesador de la cubicación de la licitación en segundo plano. Cada <c>Cubicador:IntervaloTickSegundos</c> lee la
/// cola (rutas <c>pendiente</c> con su turno vencido) y las procesa con hasta <c>Cubicador:MaximoParalelo</c> a la vez, cada una en
/// <b>su propio ámbito</b> de DI (un fallo no contamina a las demás). El límite real lo ponen los cupos compartidos por todas las réplicas
/// (<c>IClsCupoCubicador</c>): si no hay cupo libre, la vuelta termina y las rutas esperan a la siguiente. Al final de cada vuelta envía a
/// Nexo las licitaciones que quedaron cubicadas sin enviar (recuperación). Nunca termina por una excepción de una vuelta. No corre sin
/// <c>Cubicador:UrlBase</c> o con <c>Cubicador:ProcesadorHabilitado=false</c> (lo anota una vez).
/// </summary>
public sealed class clsWorkerCubicacionLicitacion(
    IServiceScopeFactory ambitos,
    IOptions<clsOpcionesCubicador> cubicador,
    IOptions<clsOpcionesCubicacionLicitacion> opciones,
    ILogger<clsWorkerCubicacionLicitacion> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(cubicador.Value.UrlBase) || !opciones.Value.ProcesadorHabilitado)
        {
            logger.LogInformation("Cubicación de licitaciones en segundo plano desactivada en esta réplica ({Motivo}).",
                string.IsNullOrWhiteSpace(cubicador.Value.UrlBase) ? "sin Cubicador:UrlBase" : "Cubicador:ProcesadorHabilitado=false");
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(opciones.Value.IntervaloTickSegundos));
        do
        {
            try
            {
                await ProcesarLoteAsync(ambitos, logger, cotizacionId: null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló una vuelta de la cubicación de licitaciones; se reintenta en la siguiente.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Una vuelta: la cola en un ámbito, cada ruta en el suyo (hasta <c>Cubicador:MaximoParalelo</c> a la vez) y la recuperación de las
    /// cubicadas sin enviar. Devuelve cuántas rutas se procesaron (cubicadas, por reintentar o fallidas). Las pruebas la llaman
    /// directamente con <paramref name="cotizacionId"/>: solo esa cotización (la base es compartida entre pruebas).
    /// </summary>
    public static async Task<int> ProcesarLoteAsync(
        IServiceScopeFactory ambitos, ILogger logger, Guid? cotizacionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ambitos);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyList<clsRutaPorCubicar> pendientes;
        int maximo;
        await using (var ambito = ambitos.CreateAsyncScope())
        {
            maximo = ambito.ServiceProvider.GetRequiredService<IOptions<clsOpcionesCubicacionLicitacion>>().Value.MaximoParalelo;
            pendientes = await ambito.ServiceProvider.GetRequiredService<IClsCubicacionLicitacionService>().PendientesAsync(cotizacionId, cancellationToken);
        }

        var procesadas = 0;
        var sinCupo = 0;
        await Parallel.ForEachAsync(
            pendientes,
            new ParallelOptions { MaxDegreeOfParallelism = maximo, CancellationToken = cancellationToken },
            async (ruta, ct) =>
            {
                if (Volatile.Read(ref sinCupo) == 1)
                {
                    return;
                }

                try
                {
                    await using var ambito = ambitos.CreateAsyncScope();
                    var resultado = await ambito.ServiceProvider.GetRequiredService<IClsCubicacionLicitacionService>().CubicarAsync(ruta, ct);
                    if (resultado == ResultadoCubicacionRuta.SinCupo)
                    {
                        // Todas las llamadas permitidas están en curso (en esta u otras réplicas): el resto espera a la siguiente vuelta.
                        Volatile.Write(ref sinCupo, 1);
                    }
                    else if (resultado != ResultadoCubicacionRuta.NoReclamada)
                    {
                        Interlocked.Increment(ref procesadas);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // El reclamo vence solo (Cubicador:ReclamoSegundos) y la ruta vuelve a la cola.
                    logger.LogError(ex, "No se pudo cubicar la ruta {RutaId} de la cotización {CotizacionId}; vuelve a la cola cuando venza su reclamo.",
                        ruta.RutaId, ruta.CotizacionId);
                }
            });

        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            await ambito.ServiceProvider.GetRequiredService<IClsCubicacionLicitacionService>().EnviarCubicadasAsync(cotizacionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudieron enviar a Nexo las licitaciones ya cubicadas; se reintenta en la siguiente vuelta.");
        }

        return procesadas;
    }
}
