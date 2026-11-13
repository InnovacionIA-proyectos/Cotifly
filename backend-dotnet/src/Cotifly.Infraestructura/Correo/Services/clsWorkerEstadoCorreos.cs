using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Correo.Services;

/// <summary>
/// Consulta del estado de los correos encolados en MailSender (ADR-023), mismo patrón que el polling del costeo
/// (<c>clsWorkerCosteoNexo</c>): cada <c>Correo:Seguimiento:IntervaloTickSegundos</c> lee la agenda (<c>Correos.siguienteConsultaAt</c>) en
/// un ámbito de DI y procesa cada correo en <b>su propio ámbito</b>; un fallo no contamina a los demás y una excepción de una vuelta no lo
/// detiene. Varias réplicas no consultan el mismo correo a la vez (reclamo con <c>siguienteConsultaAt</c>). No corre con
/// <c>Correo:Habilitado=false</c> ni sin <c>Correo:MailSender:UrlBase</c> (lo anota una vez).
/// </summary>
public sealed class clsWorkerEstadoCorreos(
    IServiceScopeFactory ambitos,
    IOptions<clsOpcionesCorreo> opciones,
    IOptions<clsOpcionesCorreoTransporte> transporte,
    ILogger<clsWorkerEstadoCorreos> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opciones.Value.Habilitado || string.IsNullOrWhiteSpace(transporte.Value.MailSender.UrlBase))
        {
            logger.LogInformation("Consulta del estado de los correos en MailSender desactivada ({Motivo}).",
                opciones.Value.Habilitado ? "sin Correo:MailSender:UrlBase" : "Correo:Habilitado=false");
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(opciones.Value.Seguimiento.IntervaloTickSegundos));
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
                logger.LogError(ex, "Falló una vuelta de la consulta de correos en MailSender; se reintenta en la siguiente.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Una vuelta: la agenda en un ámbito y cada correo en el suyo. Devuelve cuántos se procesaron (las pruebas y los escenarios BDD la
    /// llaman directamente).
    /// </summary>
    public static async Task<int> ProcesarLoteAsync(IServiceScopeFactory ambitos, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ambitos);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyList<clsConsultaCorreoPendiente> pendientes;
        await using (var ambito = ambitos.CreateAsyncScope())
        {
            pendientes = await ambito.ServiceProvider.GetRequiredService<IClsSeguimientoCorreosService>().PendientesAsync(cancellationToken);
        }

        var procesados = 0;
        foreach (var pendiente in pendientes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var ambito = ambitos.CreateAsyncScope();
                var resultado = await ambito.ServiceProvider.GetRequiredService<IClsSeguimientoCorreosService>().ProcesarAsync(pendiente, cancellationToken);
                if (resultado != ResultadoConsultaCorreo.NoReclamado)
                {
                    procesados++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo consultar el correo {CorreoId} en MailSender; sigue con los demás.", pendiente.Id);
            }
        }

        return procesados;
    }
}
