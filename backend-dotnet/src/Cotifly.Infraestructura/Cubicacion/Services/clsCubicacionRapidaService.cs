using System.Collections.Concurrent;
using System.Diagnostics;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Cubicacion.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Opción D (D3, E2E 2026-10-16): "Calcular cubicaje" asíncrono. Cada cálculo corre en su propio ámbito de DI con el token del HOST
/// (no el de la petición: si el cliente corta, el cálculo sigue y queda para <c>GET /web/cubicacion/{id}</c>) y respeta el cupo global del
/// cubicador (<see cref="clsCupoLlamadasCubicador"/>, dentro de <see cref="clsCubicadorHttp"/>). La petición espera hasta
/// <c>Cubicador:EsperaSincronaCubicacionSegundos</c>: si termina, responde como siempre; si no, devuelve el id (202).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Es un <see cref="IHostedService"/> singleton: al apagarse cancela los cálculos en curso y los espera (apagado ordenado, sin
/// tareas sueltas).</item>
/// <item>Logs con código estable: <see cref="EventoPendiente"/>, <see cref="EventoCompletada"/>, <see cref="EventoFallida"/> y
/// <see cref="EventoLleno"/>. Un error del cubicador (un 400, una caída) deja la cubicación <c>fallida</c> con su código; una excepción
/// inesperada, <c>fallida</c> con <c>CUBICADOR_NO_DISPONIBLE</c> (el detalle solo en el log).</item>
/// <item>La captura y el recálculo siguen síncronos (solo la opción C): pasarlos a segundo plano es el siguiente paso.</item>
/// </list>
/// </remarks>
public sealed class clsCubicacionRapidaService(
    IServiceScopeFactory ambitos,
    IClsAlmacenCubicacionesRapidas almacen,
    IOptions<clsOpcionesCubicador> opciones,
    ILogger<clsCubicacionRapidaService> logger) : IClsCubicacionRapidaService, IHostedService, IDisposable
{
    public static readonly EventId EventoPendiente = new(2110, "CUBICACION_RAPIDA_EN_PROCESO");
    public static readonly EventId EventoCompletada = new(2111, "CUBICACION_RAPIDA_COMPLETADA");
    public static readonly EventId EventoFallida = new(2112, "CUBICACION_RAPIDA_FALLIDA");
    public static readonly EventId EventoLleno = new(2113, "CUBICACION_RAPIDA_SIN_ESPACIO");

    public const string MensajeLleno =
        "Hay demasiadas cubicaciones en curso en este momento: intenta de nuevo en unos segundos.";

    public const string MensajeApagando =
        "Cotifly se está reiniciando: vuelve a calcular el cubicaje en unos segundos.";

    private readonly CancellationTokenSource _apagado = new();
    private readonly ConcurrentDictionary<Guid, Task> _enCurso = new();

    /// <summary>Cálculos en segundo plano que siguen corriendo (para pruebas y diagnóstico).</summary>
    public int EnCurso => _enCurso.Count;

    public async Task<clsResultado<clsInicioCubicacionRapida>> IniciarAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_apagado.IsCancellationRequested)
        {
            return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeApagando);
        }

        if (almacen.Crear(usuarioId) is not { } registro)
        {
            logger.LogWarning(EventoLleno, "CUBICACION_RAPIDA_SIN_ESPACIO: {Maximo} cubicaciones en proceso; se responde CUBICADOR_OCUPADO.",
                opciones.Value.MaximoCubicacionesEnMemoria);
            return new clsErrorDominio(clsCodigosError.CubicadorOcupado, MensajeLleno);
        }

        var id = registro.Id;
        var copia = items.ToList();
        var tarea = Task.Run(() => EjecutarAsync(id, usuarioId, copia, claseViaje, _apagado.Token), CancellationToken.None);
        _enCurso[id] = tarea;
        _ = tarea.ContinueWith(_ => _enCurso.TryRemove(id, out Task? _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        clsResultado<clsResultadoCubicacion> resultado;
        try
        {
            // La cancelación de la petición (el cliente cortó) solo deja de esperar: el cálculo sigue con el token del host.
            resultado = await tarea.WaitAsync(TimeSpan.FromSeconds(opciones.Value.EsperaSincronaCubicacionSegundos), cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogInformation(EventoPendiente,
                "CUBICACION_RAPIDA_EN_PROCESO: la cubicación {Id} sigue en segundo plano tras {Segundos} s; se responde 202.",
                id, opciones.Value.EsperaSincronaCubicacionSegundos);
            return new clsInicioCubicacionRapida(null, id);
        }

        // Respondió dentro de la espera: nadie la va a consultar.
        almacen.Descartar(id);
        return resultado.EsExitoso ? new clsInicioCubicacionRapida(resultado.Valor, null) : resultado.Error;
    }

    public clsCubicacionRapida? Obtener(Guid usuarioId, Guid id) => almacen.Obtener(id, usuarioId);

    private async Task<clsResultado<clsResultadoCubicacion>> EjecutarAsync(
        Guid id, Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken apagado)
    {
        var cronometro = Stopwatch.StartNew();
        clsResultado<clsResultadoCubicacion> resultado;
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            resultado = await ambito.ServiceProvider.GetRequiredService<IClsCalculoCubicajeService>()
                .CalcularAsync(usuarioId, items, claseViaje, apagado);
        }
        catch (OperationCanceledException) when (apagado.IsCancellationRequested)
        {
            resultado = new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeApagando);
        }
        catch (Exception ex)
        {
            logger.LogError(EventoFallida, ex, "CUBICACION_RAPIDA_FALLIDA: la cubicación {Id} falló de forma inesperada.", id);
            resultado = new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, clsCalculoCubicajeService.MensajeNoDisponible);
        }

        if (resultado.EsExitoso)
        {
            almacen.Completar(id, resultado.Valor);
            logger.LogInformation(EventoCompletada, "CUBICACION_RAPIDA_COMPLETADA: la cubicación {Id} terminó en {Milisegundos} ms ({Resultado}).",
                id, cronometro.ElapsedMilliseconds, resultado.Valor.Resultado);
        }
        else
        {
            almacen.Fallar(id, resultado.Error);
            logger.LogWarning(EventoFallida, "CUBICACION_RAPIDA_FALLIDA: la cubicación {Id} terminó con {Codigo} en {Milisegundos} ms.",
                id, resultado.Error.Codigo, cronometro.ElapsedMilliseconds);
        }

        return resultado;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Apagado ordenado: cancela los cálculos en curso y los espera (hasta que el host deje de esperar).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _apagado.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // El contenedor ya desechó el servicio (el host se desechó sin detenerse antes): no queda nada en curso que esperar.
            return;
        }

        try
        {
            await Task.WhenAll(_enCurso.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Apagado: {EnCurso} cubicación(es) rápida(s) no terminaron a tiempo.", _enCurso.Count);
        }
    }

    public void Dispose() => _apagado.Dispose();
}
