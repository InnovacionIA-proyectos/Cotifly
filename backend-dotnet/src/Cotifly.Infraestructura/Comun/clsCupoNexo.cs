using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Comun;

/// <summary>
/// Cupo de lecturas de Nexo (HU-023.4, #159, decisión 9 del 2026-10-13). Nexo limita sus listados (<c>GET /api/catalogos/*</c>,
/// <c>GET /api/solicitudes</c>...) por ROL con ventanas de un minuto y, al pasarse, responde <c>429 DEMASIADAS_SOLICITUDES</c> con
/// <c>Retry-After: 30</c>. La partición es el rol, no el usuario: todos los comerciales de Cotifly (y los usuarios de Nexo con el mismo
/// rol) comparten el cupo. Por eso el bloqueo es uno para todo Cotifly (singleton): tras un 429, ningún GET vuelve a Nexo hasta que pase
/// el <c>Retry-After</c>. Los POST no se bloquean (el alta no está limitada y un POST nunca se reintenta).
/// </summary>
public sealed class clsCupoNexo(TimeProvider tiempo)
{
    /// <summary>Espera si Nexo responde 429 sin <c>Retry-After</c> legible (la que manda Nexo hoy).</summary>
    public static readonly TimeSpan EsperaPorDefecto = TimeSpan.FromSeconds(30);

    /// <summary>Tope de la espera: un <c>Retry-After</c> desmedido no deja a Cotifly sin Nexo más de cinco minutos.</summary>
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromMinutes(5);

    private readonly object _candado = new();
    private DateTimeOffset _bloqueadoHasta = DateTimeOffset.MinValue;

    /// <summary>Lo que falta para volver a leer de Nexo, o <c>null</c> si ya se puede.</summary>
    public TimeSpan? Restante()
    {
        lock (_candado)
        {
            var falta = _bloqueadoHasta - tiempo.GetUtcNow();
            return falta > TimeSpan.Zero ? falta : null;
        }
    }

    /// <summary>Nexo respondió 429: bloquea las lecturas durante <paramref name="espera"/> (o <see cref="EsperaPorDefecto"/>), con tope.</summary>
    public TimeSpan Bloquear(TimeSpan? espera)
    {
        var duracion = espera is { } e && e > TimeSpan.Zero ? (e > EsperaMaxima ? EsperaMaxima : e) : EsperaPorDefecto;
        lock (_candado)
        {
            var hasta = tiempo.GetUtcNow() + duracion;
            if (hasta > _bloqueadoHasta)
            {
                _bloqueadoHasta = hasta;
            }
        }

        return duracion;
    }

    /// <summary>La espera que pide el <c>Retry-After</c> de <paramref name="respuesta"/> (segundos o fecha), o <c>null</c>.</summary>
    public TimeSpan? EsperaDe(HttpResponseMessage respuesta)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        var reintento = respuesta.Headers.RetryAfter;
        if (reintento?.Delta is { } delta)
        {
            return delta;
        }

        return reintento?.Date is { } fecha ? fecha - tiempo.GetUtcNow() : null;
    }
}

/// <summary>
/// Manejador de los clientes HTTP de Nexo (<c>Nexo</c> y <c>NexoCosteo</c>) que respeta el <c>Retry-After</c> de un 429
/// (<see cref="clsCupoNexo"/>): mientras el cupo está bloqueado, un GET no sale de Cotifly y recibe un 429 local con el
/// <c>Retry-After</c> restante; los adaptadores lo tratan como "Nexo no disponible" (nunca como un rechazo de los datos). Va por fuera de
/// la tubería de resiliencia, que no reintenta los 429 (<see cref="NoReintentar429"/>): reintentar dentro del mismo minuto solo gasta
/// cupo, y esperar 30 s agotaría el tiempo de la petición.
/// </summary>
public sealed class clsCupoNexoHandler(clsCupoNexo cupo, ILogger<clsCupoNexoHandler> logger) : DelegatingHandler
{
    /// <summary>Código del 429 que arma Cotifly sin llamar a Nexo (el de Nexo es <c>DEMASIADAS_SOLICITUDES</c>).</summary>
    public const string CodigoCupoLocal = "CUPO_NEXO_AGOTADO";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var esLectura = request.Method == HttpMethod.Get;
        if (esLectura && cupo.Restante() is { } restante)
        {
            return CupoAgotado(request, restante);
        }

        var respuesta = await base.SendAsync(request, cancellationToken);
        if (respuesta.StatusCode == HttpStatusCode.TooManyRequests && esLectura)
        {
            var espera = cupo.Bloquear(cupo.EsperaDe(respuesta));
            logger.LogWarning(
                "Nexo respondió 429 a GET {Ruta}: el cupo de lecturas (compartido por rol) está agotado; no se vuelve a leer de Nexo en {Segundos} s.",
                request.RequestUri?.AbsolutePath, (int)Math.Ceiling(espera.TotalSeconds));
        }

        return respuesta;
    }

    /// <summary>
    /// Ajusta la tubería estándar para que no reintente un 429 (sí los demás fallos transitorios de antes: 408, 5xx, red y tiempo).
    /// </summary>
    public static void NoReintentar429(Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions resiliencia)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.Retry.ShouldHandle = argumentos => ValueTask.FromResult(
            argumentos.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests
            && Microsoft.Extensions.Http.Resilience.HttpClientResiliencePredicates.IsTransient(argumentos.Outcome));
    }

    private static HttpResponseMessage CupoAgotado(HttpRequestMessage request, TimeSpan restante)
    {
        var segundos = (int)Math.Ceiling(restante.TotalSeconds);
        var cuerpo = "{\"code\":\"" + CodigoCupoLocal + "\",\"message\":\"Cupo de lecturas de Nexo agotado: reintentar en "
                     + segundos.ToString(CultureInfo.InvariantCulture) + " s.\"}";
        var respuesta = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            RequestMessage = request,
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        };
        respuesta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(segundos));
        return respuesta;
    }
}
