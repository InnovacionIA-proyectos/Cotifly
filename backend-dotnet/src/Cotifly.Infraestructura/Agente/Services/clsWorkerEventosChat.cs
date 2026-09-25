using System.Threading.Channels;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>Cola acotada en memoria (<see cref="Channel{T}"/>) entre el webhook de Google Chat y el worker.</summary>
public sealed class clsColaEventosChat(IOptions<clsOpcionesGoogleChat> opciones) : IClsColaEventosChat
{
    private readonly Channel<clsEventoChat> _canal = Channel.CreateBounded<clsEventoChat>(new BoundedChannelOptions(opciones.Value.CapacidadCola)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    public bool Encolar(clsEventoChat evento)
    {
        ArgumentNullException.ThrowIfNull(evento);
        return _canal.Writer.TryWrite(evento);
    }

    public ValueTask<clsEventoChat> LeerAsync(CancellationToken cancellationToken) => _canal.Reader.ReadAsync(cancellationToken);
}

/// <summary>
/// Worker de los eventos de Google Chat (port de <c>ColaEventos._run</c> de <c>orquestador/worker.py</c>): procesa cada evento en su
/// propio ámbito de DI con <see cref="IClsProcesadorEventoChat"/>. Es resiliente: un evento que falla se registra, se alerta al space
/// de soporte (<c>GChat:SupportSpace</c>, port de <c>NotificadorErrores</c>) y el worker sigue con el siguiente.
/// </summary>
public sealed class clsWorkerEventosChat(
    IClsColaEventosChat cola,
    IServiceScopeFactory ambitos,
    IOptions<clsOpcionesGoogleChat> opciones,
    ILogger<clsWorkerEventosChat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            clsEventoChat evento;
            try
            {
                evento = await cola.LeerAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ProcesarAsync(evento, stoppingToken);
        }
    }

    /// <summary>Procesa un evento sin dejar escapar sus errores (salvo el apagado del host).</summary>
    public async Task ProcesarAsync(clsEventoChat evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            await ambito.ServiceProvider.GetRequiredService<IClsProcesadorEventoChat>().ProcesarAsync(evento, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Apagado del host: el evento en curso se abandona.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo procesando el evento de Google Chat {Tipo} de {Space}.", evento.Tipo, evento.SpaceId);
            await AlertarAsync(evento, ex, cancellationToken);
        }
    }

    private async Task AlertarAsync(clsEventoChat evento, Exception error, CancellationToken cancellationToken)
    {
        var soporte = opciones.Value.SupportSpace;
        if (string.IsNullOrWhiteSpace(soporte))
        {
            return;
        }

        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var publicador = ambito.ServiceProvider.GetRequiredService<IClsPublicadorChat>();
            await publicador.PublicarAsync(soporte.Trim(), null, clsTarjetasChat.Alerta($"evento {evento.Tipo} · {evento.SpaceId}", $"{error.GetType().Name}: {error.Message}"), cancellationToken);
        }
        catch (Exception alerta) when (alerta is not OperationCanceledException)
        {
            // La alerta es best-effort: nunca tumba al worker.
            logger.LogWarning(alerta, "No se pudo publicar la alerta en el space de soporte.");
        }
    }
}

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
