using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// DolarApi falso (<c>GET /v1/trm</c>, HU-012.1, ADR-014): un <see cref="HttpMessageHandler"/> que nunca sale a la red. Por defecto
/// responde la TRM de <see cref="Valor"/> publicada en <see cref="FechaActualizacion"/> con el formato real
/// (<c>{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T19:01:18.476Z"}</c>). Registra cada petición y
/// permite encolar respuestas o fallos.
/// </summary>
public sealed class clsDolarApiFalso : HttpMessageHandler
{
    /// <summary>URL en las pruebas (la real es <c>https://co.dolarapi.com/v1/trm</c>).</summary>
    public const string Url = "https://dolarapi-prueba.local/v1/trm";

    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _respuestas = new();
    private readonly ConcurrentQueue<Uri> _peticiones = new();

    /// <summary>TRM que responde por defecto.</summary>
    public decimal Valor { get; set; } = 3273.49m;

    /// <summary>Instante de publicación que responde por defecto (14:01 del 2026-10-05 en Bogotá).</summary>
    public DateTimeOffset FechaActualizacion { get; set; } = new(2026, 10, 5, 19, 1, 18, 476, TimeSpan.Zero);

    /// <summary>URLs recibidas, en orden.</summary>
    public IReadOnlyList<Uri> Peticiones => [.. _peticiones];

    /// <summary>Cuerpo en el formato real de DolarApi.</summary>
    public static string Cuerpo(decimal valor, DateTimeOffset fechaActualizacion, string unidad = "COP") =>
        "{\"moneda\":\"USD\",\"casa\":\"oficial\",\"nombre\":\"TRM\",\"unidad\":\"" + unidad + "\",\"valor\":"
        + valor.ToString(CultureInfo.InvariantCulture) + ",\"fechaActualizacion\":\""
        + fechaActualizacion.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "\"}";

    /// <summary>Encola una respuesta fija (se consume una por petición; sin cola se responde con <see cref="Valor"/>).</summary>
    public clsDolarApiFalso Responder(HttpStatusCode estado, string? json = null)
    {
        _respuestas.Enqueue(_ => new HttpResponseMessage(estado)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>Encola un fallo de red o de tiempo (la excepción se lanza al enviar).</summary>
    public clsDolarApiFalso Fallar(Exception excepcion)
    {
        _respuestas.Enqueue(_ => throw excepcion);
        return this;
    }

    /// <summary>Encola <paramref name="veces"/> respuestas 503 (para agotar los reintentos).</summary>
    public clsDolarApiFalso Caida(int veces = 5)
    {
        for (var i = 0; i < veces; i++)
        {
            Responder(HttpStatusCode.ServiceUnavailable);
        }

        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _peticiones.Enqueue(request.RequestUri!);

        if (_respuestas.TryDequeue(out var respuesta))
        {
            return Task.FromResult(respuesta(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Cuerpo(Valor, FechaActualizacion), Encoding.UTF8, "application/json"),
        });
    }
}
