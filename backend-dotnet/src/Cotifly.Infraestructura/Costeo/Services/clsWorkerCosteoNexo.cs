using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Polling del costeo en Nexo (HU-005.6, ADR-015 §4): cada <c>CosteoNexo:IntervaloTickSegundos</c> lee la agenda
/// (<c>costeoSiguienteConsultaAt</c>) en un ámbito de DI y procesa cada cotización en <b>su propio ámbito</b>, así un fallo (de Nexo o
/// de la base) no contamina a las demás. Tolera a Nexo caído (el caso de uso reprograma con espaciado) y nunca termina por una
/// excepción de una vuelta. No corre sin <c>Nexo:UrlBase</c> o con <c>CosteoNexo:Habilitado=false</c> (lo anota una vez).
/// </summary>
public sealed class clsWorkerCosteoNexo(
    IServiceScopeFactory ambitos,
    IOptions<clsOpcionesNexo> nexo,
    IOptions<clsOpcionesCosteoNexo> opciones,
    ILogger<clsWorkerCosteoNexo> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(nexo.Value.UrlBase) || !opciones.Value.Habilitado)
        {
            logger.LogInformation("Polling del costeo en Nexo desactivado ({Motivo}).",
                string.IsNullOrWhiteSpace(nexo.Value.UrlBase) ? "sin Nexo:UrlBase" : "CosteoNexo:Habilitado=false");
            return;
        }

        await RepararEntregadasSinPrecioAsync(ambitos, logger, stoppingToken);

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(opciones.Value.IntervaloTickSegundos));
        do
        {
            try
            {
                await ProcesarLoteAsync(ambitos, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló una vuelta del polling del costeo en Nexo; se reintenta en la siguiente.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Reparación de HU-013.14 (ADR-018 §17), una vez al arrancar: agenda la relectura de las cotizaciones que quedaron en
    /// <c>entregada</c> sin precio. Un fallo se anota y no impide el polling.
    /// </summary>
    public static async Task RepararEntregadasSinPrecioAsync(IServiceScopeFactory ambitos, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ambitos);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            await ambito.ServiceProvider.GetRequiredService<IClsPollingCosteoService>().ReprogramarEntregadasSinPrecioAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo agendar la relectura de las cotizaciones entregadas sin precio; el polling sigue.");
        }
    }

    /// <summary>
    /// Una vuelta del polling: la agenda en un ámbito y cada cotización en el suyo. Devuelve cuántas se procesaron (las pruebas y
    /// los escenarios BDD la llaman directamente).
    /// </summary>
    public static async Task<int> ProcesarLoteAsync(IServiceScopeFactory ambitos, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ambitos);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyList<Aplicacion.Cotizaciones.DTOs.clsConsultaCosteoPendiente> pendientes;
        await using (var ambito = ambitos.CreateAsyncScope())
        {
            pendientes = await ambito.ServiceProvider.GetRequiredService<IClsPollingCosteoService>().PendientesAsync(cancellationToken);
        }

        var procesadas = 0;
        foreach (var pendiente in pendientes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var ambito = ambitos.CreateAsyncScope();
                var resultado = await ambito.ServiceProvider.GetRequiredService<IClsPollingCosteoService>().ProcesarAsync(pendiente, cancellationToken);
                if (resultado != ResultadoConsultaCosteo.NoReclamada)
                {
                    procesadas++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo procesar la cotización {CotizacionId} en el polling de Nexo; sigue con las demás.", pendiente.Id);
            }
        }

        return procesadas;
    }
}
