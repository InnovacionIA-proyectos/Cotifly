using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Nexo falso para el buscador de clientes: un <see cref="HttpMessageHandler"/> que nunca sale a la red.
/// Registra cada petición (URL y cabecera <c>X-Portal-clsUsuario-Id</c>) y permite encolar respuestas o fallos. Como Nexo, responde 401
/// <c>SESION_INVALIDA</c> a una petición sin esa cabecera.
/// </summary>
public sealed class clsNexoClientesFalso : HttpMessageHandler
{
    /// <summary>URL base del servicio en las pruebas: la raíz de Nexo, sin <c>/api</c> (el código agrega <c>/api/...</c>, ADR-015 §8).</summary>
    public const string UrlBase = "http://nexo-test.local";

    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _respuestas = new();
    private readonly ConcurrentQueue<clsPeticionNexo> _peticiones = new();

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<clsPeticionNexo> Peticiones => [.. _peticiones];

    /// <summary>Encola una respuesta fija (se consume una por petición).</summary>
    public clsNexoClientesFalso Responder(HttpStatusCode estado, string? json = null)
    {
        _respuestas.Enqueue(_ => new HttpResponseMessage(estado)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>Encola el 429 del rate limiter de Nexo (<c>DEMASIADAS_SOLICITUDES</c>) con <c>Retry-After: {segundos}</c>.</summary>
    public clsNexoClientesFalso ResponderLimite(int segundos = 30)
    {
        _respuestas.Enqueue(_ =>
        {
            var respuesta = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"code":"DEMASIADAS_SOLICITUDES","message":"Ha alcanzado el límite de solicitudes para este recurso."}""",
                    Encoding.UTF8, "application/json"),
            };
            respuesta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(segundos));
            return respuesta;
        });
        return this;
    }

    /// <summary>Encola una respuesta 200 con el array <c>ClienteBusquedaDto[]</c> de Nexo.</summary>
    public clsNexoClientesFalso ResponderCandidatos(string json) => Responder(HttpStatusCode.OK, json);

    /// <summary>Encola un fallo de red o de tiempo (la excepción se lanza al enviar).</summary>
    public clsNexoClientesFalso Fallar(Exception excepcion)
    {
        _respuestas.Enqueue(_ => throw excepcion);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var identidad = request.Headers.TryGetValues(clsPeticionNexo.EncabezadoIdentidad, out var valores) ? valores.FirstOrDefault() : null;
        _peticiones.Enqueue(new clsPeticionNexo(
            request.Method,
            request.RequestUri!,
            identidad,
            request.Headers.Accept.ToString(),
            cuerpo));
        if (!long.TryParse(identidad, out _))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"code":"SESION_INVALIDA","message":"La sesión no es válida."}""", Encoding.UTF8, "application/json"),
            };
        }

        var respuesta = _respuestas.TryDequeue(out var fabrica) ? fabrica(request) : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        };
        return respuesta;
    }
}

/// <summary>Petición recibida por <see cref="clsNexoClientesFalso"/>. <paramref name="Cuerpo"/> es <c>null</c> en un GET sin cuerpo.</summary>
public sealed record clsPeticionNexo(HttpMethod Metodo, Uri Url, string? IdPortalUsuario, string Accept, string? Cuerpo = null)
{
    /// <summary>
    /// Encabezado de identidad que exige Nexo (<c>clsIdentidadPortalAuthenticationHandler.HeaderIdUsuario</c>). Escrito aquí a mano, no
    /// tomado del código de Cotifly: si Cotifly mandara otro nombre, los falsos responderían 401 como Nexo.
    /// </summary>
    public const string EncabezadoIdentidad = "X-Portal-clsUsuario-Id";
}
