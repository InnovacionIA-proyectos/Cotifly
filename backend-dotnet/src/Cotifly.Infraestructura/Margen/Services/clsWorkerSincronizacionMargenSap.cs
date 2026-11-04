using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Worker de la sincronización del margen de SAP TM (HU-042.2, #243, ADR-027; reemplaza el SQL Agent Job de la #242). Mismo molde que
/// <c>clsWorkerCosteoNexo</c> y <c>clsLimpiezaIdempotenciaHostedService</c>: <see cref="PeriodicTimer"/> con <see cref="TimeProvider"/>, una
/// ronda por chequeo en su propio ámbito de DI y ninguna excepción tumba el host.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Con <c>SapTm:SincronizacionHabilitada = false</c> (por defecto) no corre (una línea Information). Sin <c>SapTm:Url</c>, <c>User</c> o
/// <c>Pass</c> no corre (una línea Information) y el margen sigue con la copia local o la política.
/// Con la URL o el mandante mal escritos tampoco corre (una línea Error), pero la API arranca.</item>
/// <item>La primera ronda espera <see cref="EsperaInicial"/>: no retrasa el arranque ni <c>/health/ready</c>. Después, cada
/// <c>SapTm:ChequeoMinutos</c> (60) revisa la última corrida <c>ok</c> y sincroniza solo si tiene más de <c>SapTm:SincronizacionMinutos</c>
/// (1440) o si nunca la hubo.</item>
/// <item>Con varias réplicas cada una revisa; el <c>sp_getapplock</c> del stored procedure evita que dos corridas se pisen (la segunda
/// queda <c>omitido</c>) y la corrida <c>ok</c> de una hace que a las demás ya no les toque.</item>
/// <item>En Production, una <c>SapTm:Url</c> http (red interna) se acepta con un warning al arrancar.</item>
/// </list>
/// </remarks>
public sealed class clsWorkerSincronizacionMargenSap(
    IServiceScopeFactory ambitos,
    IOptions<clsOpcionesSapTmSincronizacion> opciones,
    clsCoordinacionSincronizacionMargenSap coordinacion,
    IHostEnvironment entorno,
    TimeProvider tiempo,
    ILogger<clsWorkerSincronizacionMargenSap> logger) : BackgroundService
{
    /// <summary>Espera antes de la primera ronda: la API termina de arrancar primero.</summary>
    public static readonly TimeSpan EsperaInicial = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var valor = opciones.Value;
        if (!valor.SincronizacionHabilitada)
        {
            logger.LogInformation(
                "Sincronización del margen de SAP TM deshabilitada (SapTm:SincronizacionHabilitada = false). El margen sigue con la copia local existente o la política de la línea.");
            return;
        }

        if (!valor.TieneCredenciales)
        {
            logger.LogInformation(
                "Sincronización del margen de SAP TM inactiva: falta SapTm:Url, SapTm:User o SapTm:Pass. El margen sigue con la copia local existente o la política de la línea.");
            return;
        }

        if (valor.ErrorConfiguracion is { } error)
        {
            // No impide arrancar la API: el margen no depende de SAP al cotizar. La administración responde SAP_MARGEN_NO_CONFIGURADO.
            logger.LogError("Sincronización del margen de SAP TM inactiva por configuración inválida: {Error} Corríjala y reinicie.", error);
            return;
        }

        if (entorno.IsProduction() && Uri.TryCreate(valor.UrlServicio, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttp)
        {
            logger.LogWarning(
                "SapTm:Url usa http (sin TLS): se acepta solo en la red interna; la clave del usuario técnico de SAP viaja en Basic sin cifrar.");
        }

        var chequeo = TimeSpan.FromMinutes(valor.ChequeoMinutos);
        logger.LogInformation("Sincronización del margen de SAP TM activa: chequeo cada {Chequeo} min, intervalo {Intervalo} min.",
            valor.ChequeoMinutos, valor.SincronizacionMinutos);
        try
        {
            coordinacion.ProximoChequeo = tiempo.GetUtcNow() + EsperaInicial;
            await Task.Delay(EsperaInicial, tiempo, stoppingToken);
            using var temporizador = new PeriodicTimer(chequeo, tiempo);
            do
            {
                coordinacion.ProximoChequeo = tiempo.GetUtcNow() + chequeo;
                await RondaAsync(stoppingToken);
            }
            while (await temporizador.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado de la API.
        }
        finally
        {
            coordinacion.ProximoChequeo = null;
        }
    }

    /// <summary>
    /// Una ronda en su propio ámbito: sincroniza si toca. Devuelve el resultado, o <c>null</c> si la ronda falló (queda en Error y se reintenta
    /// en el chequeo siguiente). Las pruebas la llaman directamente.
    /// </summary>
    public async Task<clsResultadoSincronizacionMargenSap?> RondaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            return await ambito.ServiceProvider.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Falló una ronda de la sincronización del margen de SAP TM ({Tipo}); se reintenta en el chequeo siguiente.", ex.GetType().Name);
            logger.LogDebug(ex, "Detalle del fallo de la ronda de la sincronización del margen de SAP TM.");
            return null;
        }
    }
}
