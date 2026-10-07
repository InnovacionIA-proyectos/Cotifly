using Cotifly.Aplicacion.Agente.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>
/// Archiva las sesiones inactivas (port de <c>_barrido_sesiones</c> y <c>archivar_inactivas</c> del Python): cada
/// <c>Sesiones:BarridoMinutos</c>, las activas sin actividad en <c>Sesiones:TtlHoras</c> pasan a <c>archivada</c> (la traza queda en
/// la base). Un fallo es un warning; el barrido nunca tumba el servicio.
/// </summary>
public sealed class clsBarridoSesionesHostedService(
    IServiceScopeFactory ambitos,
    IOptions<clsOpcionesSesiones> opciones,
    TimeProvider tiempo,
    ILogger<clsBarridoSesionesHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutos = opciones.Value.BarridoMinutos;
        if (minutos <= 0)
        {
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromMinutes(minutos), tiempo);
        try
        {
            while (await temporizador.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var archivadas = await BarrerAsync(stoppingToken);
                    if (archivadas > 0)
                    {
                        logger.LogInformation("Barrido de sesiones: {Archivadas} archivadas por inactividad.", archivadas);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Fallo en el barrido de sesiones inactivas.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado del host.
        }
    }

    /// <summary>Un barrido: archiva las sesiones activas sin actividad desde hace más de <c>Sesiones:TtlHoras</c>. Devuelve cuántas.</summary>
    public async Task<int> BarrerAsync(CancellationToken cancellationToken = default)
    {
        await using var ambito = ambitos.CreateAsyncScope();
        var servicios = ambito.ServiceProvider;
        var reloj = servicios.GetRequiredService<Aplicacion.Comun.Interfaces.IClsReloj>();
        var sesiones = servicios.GetRequiredService<Aplicacion.Agente.Repositories.IClsSesionRepository>();
        var umbral = reloj.AhoraUtc - TimeSpan.FromHours(opciones.Value.TtlHoras);
        var inactivas = await sesiones.ListarInactivasAsync(umbral, cancellationToken);
        foreach (var sesion in inactivas)
        {
            sesion.Archivar();
        }

        if (inactivas.Count > 0)
        {
            await servicios.GetRequiredService<Aplicacion.Comun.Interfaces.IClsUnitOfWork>().GuardarCambiosAsync(cancellationToken);
        }

        return inactivas.Count;
    }
}
