using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Datos abiertos de la Superintendencia Financiera falsos (conjunto <c>32sa-8pi3</c>, HU-012.1): un <see cref="HttpMessageHandler"/> que
/// nunca sale a la red. Por defecto responde como SODA: interpreta el <c>$where</c> recibido y devuelve la fila de <see cref="Historico"/>
/// cuyo rango cubre la fecha, con el mismo formato real (<c>"valor":"3306.86"</c>, fechas con <c>.000</c>). Registra cada petición y
/// permite encolar respuestas o fallos.
/// </summary>
public sealed partial class clsTrmFalso : HttpMessageHandler
{
    /// <summary>URL del recurso en las pruebas (la real es <c>https://www.datos.gov.co/resource/32sa-8pi3.json</c>).</summary>
    public const string Url = "https://datos-abiertos-prueba.local/resource/32sa-8pi3.json";

    /// <summary>TRM de la semana del 2026-09-21 (la del viernes 25 cubre sábado, domingo y lunes, como en la fuente real).</summary>
    public static readonly IReadOnlyList<(decimal Valor, DateOnly Desde, DateOnly Hasta)> Historico =
    [
        (3290.10m, new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 24)),
        (3301.45m, new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25)),
        (3306.86m, new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 28)),
        (3312.50m, new DateOnly(2026, 9, 29), new DateOnly(2026, 9, 29)),
    ];

    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _respuestas = new();
    private readonly ConcurrentQueue<Uri> _peticiones = new();

    /// <summary>URLs recibidas, en orden.</summary>
    public IReadOnlyList<Uri> Peticiones => [.. _peticiones];

    /// <summary>Encola una respuesta fija (se consume una por petición; sin cola se responde desde <see cref="Historico"/>).</summary>
    public clsTrmFalso Responder(HttpStatusCode estado, string? json = null)
    {
        _respuestas.Enqueue(_ => new HttpResponseMessage(estado)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>Encola un fallo de red o de tiempo (la excepción se lanza al enviar).</summary>
    public clsTrmFalso Fallar(Exception excepcion)
    {
        _respuestas.Enqueue(_ => throw excepcion);
        return this;
    }

    /// <summary>Encola <paramref name="veces"/> respuestas 503 (para agotar los reintentos).</summary>
    public clsTrmFalso Caida(int veces = 5)
    {
        for (var i = 0; i < veces; i++)
        {
            Responder(HttpStatusCode.ServiceUnavailable);
        }

        return this;
    }

    /// <summary>Fila en el formato real de SODA.</summary>
    public static string Fila(decimal valor, DateOnly desde, DateOnly hasta) => JsonSerializer.Serialize(new
    {
        valor = valor.ToString(CultureInfo.InvariantCulture),
        unidad = "COP",
        vigenciadesde = desde.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000",
        vigenciahasta = hasta.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000",
    });

    /// <summary>Parámetro de la consulta, decodificado (<c>$where</c>, <c>$order</c>...).</summary>
    public static string? Parametro(Uri url, string nombre)
    {
        ArgumentNullException.ThrowIfNull(url);
        foreach (var parte in url.Query.TrimStart('?').Split('&'))
        {
            if (parte.StartsWith(nombre + "=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parte[(nombre.Length + 1)..]);
            }
        }

        return null;
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

        var filtro = Parametro(request.RequestUri!, "$where");
        var coincidencia = filtro is null ? null : PatronFiltro().Match(filtro);
        if (coincidencia is not { Success: true } || coincidencia.Groups[1].Value != coincidencia.Groups[2].Value)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        var fecha = DateOnly.ParseExact(coincidencia.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filas = Historico.Where(h => h.Desde <= fecha && h.Hasta >= fecha).Select(h => Fila(h.Valor, h.Desde, h.Hasta));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[" + string.Join(",", filas) + "]", Encoding.UTF8, "application/json"),
        });
    }

    [GeneratedRegex(@"^vigenciadesde <= '(\d{4}-\d{2}-\d{2})T00:00:00' AND vigenciahasta >= '(\d{4}-\d{2}-\d{2})T00:00:00'$")]
    private static partial Regex PatronFiltro();
}
