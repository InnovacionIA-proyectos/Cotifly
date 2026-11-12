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
/// "Calcular cubicaje" asíncrono. Cada cálculo corre en su propio ámbito de DI con el token del HOST
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
/// <item>La captura y el recálculo son síncronos.</item>
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

    // B1: el registro de un cálculo nuevo y el inicio del apagado se excluyen. Una petición que entra durante StopAsync/Dispose ve
    // _cerrado y responde CUBICADOR_NO_DISPONIBLE (503) sin tocar _apagado.Token (la fuente puede estar desechada), y StopAsync espera
    // la foto de _enCurso tomada después de cerrar: ninguna tarea se registra tras esa foto.
    private readonly Lock _cerrojo = new();
    private bool _cerrado;
    private bool _desechado;

    /// <summary>Cálculos en segundo plano que siguen corriendo (para pruebas y diagnóstico).</summary>
    public int EnCurso => _enCurso.Count;

    public async Task<clsResultado<clsInicioCubicacionRapida>> IniciarAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copia = items.ToList();
        Guid id;
        Task<clsResultado<clsResultadoCubicacion>> tarea;
        lock (_cerrojo)
        {
            if (_cerrado)
            {
                return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeApagando);
            }

            if (almacen.Crear(usuarioId) is not { } registro)
            {
                logger.LogWarning(EventoLleno, "CUBICACION_RAPIDA_SIN_ESPACIO: {Maximo} cubicaciones en proceso; se responde CUBICADOR_OCUPADO.",
                    opciones.Value.MaximoCubicacionesEnMemoria);
                return new clsErrorDominio(clsCodigosError.CubicadorOcupado, MensajeLleno);
            }

            id = registro.Id;
            var tokenApagado = _apagado.Token; // Seguro: con _cerrado en false la fuente no se ha desechado.
            tarea = Task.Run(() => EjecutarAsync(id, usuarioId, copia, claseViaje, tokenApagado), CancellationToken.None);

            // _enCurso guarda la tarea de LIMPIEZA (no la de cálculo): así StopAsync, que espera los valores de _enCurso, no puede
            // devolver hasta que el TryRemove ya corrió. Dos continuaciones independientes sobre `tarea` (esta y el WhenAll de
            // StopAsync) no tienen orden garantizado entre sí; encadenarlas en una sola si lo garantiza.
            var idRegistrado = id;
            _enCurso[id] = tarea.ContinueWith(_ => _enCurso.TryRemove(idRegistrado, out Task? _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

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

    /// <summary>
    /// Apagado ordenado: cierra la entrada (las peticiones nuevas reciben <c>CUBICADOR_NO_DISPONIBLE</c>), cancela los cálculos en curso
    /// y los espera (hasta que el host deje de esperar).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pendientes;
        lock (_cerrojo)
        {
            if (_desechado)
            {
                // El contenedor ya desechó el servicio (el host se desechó sin detenerse antes): no queda nada en curso que esperar.
                return;
            }

            _cerrado = true;
            // Tras cerrar no se registra ninguna tarea más: esta foto incluye la última que entró.
            pendientes = [.. _enCurso.Values];
        }

        try
        {
            await _apagado.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Dispose concurrente con StopAsync: las tareas ya tienen su token y la fuente cancelada o desechada; se esperan igual.
        }

        try
        {
            await Task.WhenAll(pendientes).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Apagado: {EnCurso} cubicación(es) rápida(s) no terminaron a tiempo.", _enCurso.Count);
        }
    }

    public void Dispose()
    {
        lock (_cerrojo)
        {
            if (_desechado)
            {
                return;
            }

            _cerrado = true;
            _desechado = true;
        }

        try
        {
            // Si nadie llamó StopAsync (o perdió la carrera con Dispose), los cálculos en curso se cancelan igual.
            _apagado.Cancel();
        }
        catch (AggregateException ex)
        {
            logger.LogWarning(ex, "Apagado: una cancelación de cubicación rápida falló al desechar el servicio.");
        }

        _apagado.Dispose();
    }
}
