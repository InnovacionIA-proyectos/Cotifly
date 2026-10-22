using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Diagnostics;

namespace Cotifly.Api.Comun;

/// <summary>
/// Endurecimiento A (#199): manejador global de las excepciones que ningún caso de uso controló. Sin él, un 500 salía sin cuerpo (o con la
/// página de desarrollo). Responde siempre la misma forma del contrato, sin detalles técnicos ni la traza:
/// <list type="bullet">
/// <item><c>/web/*</c>, <c>/pdfs/*</c> y el resto: 500 <c>ErrorNegocio</c> <c>{"error": {"codigo": "ERROR_INTERNO", "mensaje": "...", "detalle": {"id_correlacion": "..."}}}</c>.</item>
/// <item><c>/api/*</c> (login, refresh, logout): 500 <c>ErrorWeb</c> <c>{"error": "..."}</c>, la única forma de error de ese grupo.</item>
/// </list>
/// La excepción queda en el log UNA vez, con el id de correlación de la petición (<see cref="HttpContext.TraceIdentifier"/>, el mismo
/// <c>RequestId</c> del ámbito de los demás logs de la petición), el método y la ruta (sin la consulta). No se responde nada si la petición
/// la abortó el cliente (<see cref="OperationCanceledException"/> con <c>RequestAborted</c>): no hay a quién responderle y no es un error del
/// servidor. Un <see cref="BadHttpRequestException"/> (por ejemplo, el cuerpo sin <c>Content-Length</c> que pasa el límite y Kestrel corta)
/// conserva su estado (413, 400...) con <c>ErrorWeb</c>, como antes de existir este manejador.
/// </summary>
public static partial class clsManejadorErroresNoControlados
{
    /// <summary>Mensaje genérico del 500: nunca el de la excepción.</summary>
    public const string MensajeErrorInterno = "Ocurrió un error interno. Intenta de nuevo y, si se repite, contacta a soporte.";

    /// <summary>Registra el manejador como primer middleware de la tubería: atrapa lo que escapa de todo lo demás.</summary>
    public static WebApplication UseManejadorErroresNoControlados(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = ManejarAsync,
            // El middleware ya no escribe su propio log ("An unhandled exception..."): este manejador registra la excepción una vez,
            // con el id de correlación.
            SuppressDiagnosticsCallback = _ => true,
        });
        return app;
    }

    /// <summary>Escribe la respuesta del error no controlado de la petición (público para las pruebas).</summary>
    public static async Task ManejarAsync(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var excepcion = contexto.Features.Get<IExceptionHandlerFeature>()?.Error;
        var logger = contexto.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(clsManejadorErroresNoControlados).FullName!);
        var ruta = contexto.Request.Path.Value ?? string.Empty;

        if (excepcion is OperationCanceledException && contexto.RequestAborted.IsCancellationRequested)
        {
            // El cliente cerró la conexión: nada que responder (499 solo para el log de acceso).
            LogPeticionAbortada(logger, contexto.TraceIdentifier, contexto.Request.Method, ruta);
            contexto.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return;
        }

        clsSeguridadHttp.AplicarEncabezadosSeguridad(contexto);
        if (excepcion is BadHttpRequestException peticionInvalida)
        {
            // Kestrel rechazó la petición (cuerpo que pasa el límite, cuerpo cortado...): es un error del cliente, no un 500.
            contexto.Response.StatusCode = peticionInvalida.StatusCode;
            var mensaje = peticionInvalida.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? clsSeguridadHttp.MensajeCuerpoDemasiadoGrande
                : clsErrorWeb.MensajeCuerpoInvalido;
            await contexto.Response.WriteAsJsonAsync(new clsErrorWeb(mensaje), CancellationToken.None);
            return;
        }

        LogErrorNoControlado(logger, excepcion, contexto.TraceIdentifier, contexto.Request.Method, ruta);
        contexto.Response.StatusCode = StatusCodes.Status500InternalServerError;
        if (contexto.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await contexto.Response.WriteAsJsonAsync(new clsErrorWeb(MensajeErrorInterno), CancellationToken.None);
            return;
        }

        await contexto.Response.WriteAsJsonAsync(CuerpoErrorInterno(contexto.TraceIdentifier), CancellationToken.None);
    }

    /// <summary>Clave de <c>detalle</c> con el id de correlación de la petición (el mismo del log), para soporte.</summary>
    public const string ClaveIdCorrelacion = "id_correlacion";

    /// <summary>
    /// El cuerpo <c>ErrorNegocio</c> del 500: código <c>ERROR_INTERNO</c>, el mensaje genérico y, como único <c>detalle</c>, el id de
    /// correlación (<see cref="HttpContext.TraceIdentifier"/>): no expone datos y permite a soporte encontrar la excepción en el log.
    /// </summary>
    public static clsErrorNegocioWeb CuerpoErrorInterno(string? idCorrelacion) =>
        new(new clsErrorNegocioWeb.clsCuerpo(
            clsCodigosError.ErrorInterno,
            MensajeErrorInterno,
            string.IsNullOrWhiteSpace(idCorrelacion) ? null : new Dictionary<string, object?>(StringComparer.Ordinal) { [ClaveIdCorrelacion] = idCorrelacion }));

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error,
        Message = "Error no controlado en {Metodo} {Ruta} (id de correlación {IdCorrelacion}): se respondió 500 ERROR_INTERNO.")]
    private static partial void LogErrorNoControlado(ILogger logger, Exception? excepcion, string idCorrelacion, string metodo, string ruta);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug,
        Message = "Petición {Metodo} {Ruta} abortada por el cliente (id de correlación {IdCorrelacion}): no se responde.")]
    private static partial void LogPeticionAbortada(ILogger logger, string idCorrelacion, string metodo, string ruta);
}
