using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// MailSender falso para las pruebas: un <see cref="HttpMessageHandler"/> que nunca sale a la red y responde con la forma
/// del microservicio real (<c>InnovacionIA/MailSender</c>): <c>POST /api/v1/emails</c> (multipart, 202 <c>{id, status, statusUrl}</c>),
/// <c>GET /api/v1/emails/{id}</c> (<c>EmailStatusResponse</c> con <c>status</c> numérico) y <c>POST /api/v1/emails/{id}/retry</c>. Las pruebas
/// cambian el estado de cada correo (<see cref="Entregar"/>, <see cref="Fallar"/>, <see cref="Olvidar"/>) y simulan rechazos (400/413/422),
/// caídas y tiempo agotado. NUNCA envía correos reales. Seguro para varios hilos.
/// </summary>
public sealed class clsMailSenderFalso : HttpMessageHandler
{
    /// <summary>Raíz del servicio en las pruebas.</summary>
    public const string UrlBase = "http://mailsender.pruebas.local";

    /// <summary>Buzón de pruebas de las pruebas (<c>Correo:Proteccion:BuzonPruebas</c>).</summary>
    public const string BuzonPruebas = "cotifly-pruebas@pruebas.local";

    private readonly ConcurrentDictionary<Guid, clsCorreoFalso> _correos = new();
    private readonly ConcurrentQueue<string> _correlaciones = new();
    private int _reintentos;
    private int _consultas;

    /// <summary>Correos aceptados, en orden.</summary>
    public IReadOnlyList<clsCorreoFalso> Recibidos => [.. _correos.Values.OrderBy(c => c.Orden)];

    /// <summary>Encabezados <c>X-Correlation-ID</c> recibidos, en orden.</summary>
    public IReadOnlyList<string> Correlaciones => [.. _correlaciones];

    /// <summary>Llamadas a <c>/retry</c>.</summary>
    public int Reintentos => Volatile.Read(ref _reintentos);

    /// <summary>Consultas de estado (<c>GET /api/v1/emails/{id}</c>).</summary>
    public int Consultas => Volatile.Read(ref _consultas);

    /// <summary>Si tiene valor, el alta responde este estado con un <c>ProblemDetails</c> (400, 413, 422, 500...).</summary>
    public HttpStatusCode? AltaFalla { get; set; }

    /// <summary>Si es verdadero, toda petición falla como un servicio caído (conexión rechazada).</summary>
    public bool Caido { get; set; }

    /// <summary>Si tiene valor, cada petición espera este tiempo antes de responder (tiempo agotado del cliente).</summary>
    public TimeSpan? Demora { get; set; }

    /// <summary>MailSender entregó el correo al relay (<c>Sent</c>, 3).</summary>
    public void Entregar(Guid id, int intentos = 1) => Cambiar(id, c => { c.Estado = 3; c.Intentos = intentos; c.UltimoError = null; c.EnviadoAt = DateTimeOffset.UtcNow; });

    /// <summary>MailSender agotó los reintentos (<c>Failed</c>, 5) con el error del relay.</summary>
    public void Fallar(Guid id, string error = "550 5.7.1 Unable to relay", int intentos = 5) =>
        Cambiar(id, c => { c.Estado = 5; c.Intentos = intentos; c.UltimoError = error; });

    /// <summary>Error transitorio con un reintento programado (<c>RetryScheduled</c>, 4).</summary>
    public void ProgramarReintento(Guid id, string error = "421 4.3.2 Service not available") =>
        Cambiar(id, c => { c.Estado = 4; c.Intentos++; c.UltimoError = error; });

    /// <summary>MailSender ya no conoce el correo (404).</summary>
    public void Olvidar(Guid id) => _correos.TryRemove(id, out _);

    private void Cambiar(Guid id, Action<clsCorreoFalso> cambio)
    {
        if (_correos.TryGetValue(id, out var correo))
        {
            lock (correo)
            {
                cambio(correo);
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.TryGetValues("X-Correlation-ID", out var valores))
        {
            _correlaciones.Enqueue(valores.First());
        }

        if (Demora is { } demora)
        {
            await Task.Delay(demora, cancellationToken);
        }

        if (Caido)
        {
            throw new HttpRequestException("Connection refused (mailsender.pruebas.local:80)");
        }

        var ruta = request.RequestUri!.AbsolutePath.TrimEnd('/');
        if (request.Method == HttpMethod.Post && ruta == "/api/v1/emails")
        {
            return await AltaAsync(request);
        }

        if (ruta.StartsWith("/api/v1/emails/", StringComparison.Ordinal))
        {
            var resto = ruta["/api/v1/emails/".Length..];
            if (request.Method == HttpMethod.Post && resto.EndsWith("/retry", StringComparison.Ordinal)
                && Guid.TryParse(resto[..^"/retry".Length], out var idReintento))
            {
                Interlocked.Increment(ref _reintentos);
                if (!_correos.ContainsKey(idReintento))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                Cambiar(idReintento, c => { c.Estado = 1; c.UltimoError = null; });
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            if (request.Method == HttpMethod.Get && Guid.TryParse(resto, out var id))
            {
                Interlocked.Increment(ref _consultas);
                return _correos.TryGetValue(id, out var correo) ? Json(HttpStatusCode.OK, Estado(correo)) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> AltaAsync(HttpRequestMessage request)
    {
        if (AltaFalla is { } falla)
        {
            return Json(falla, new
            {
                type = "https://tools.ietf.org/html/rfc9110",
                title = falla == HttpStatusCode.RequestEntityTooLarge ? "Payload too large." : "Email request is invalid.",
                status = (int)falla,
                errors = new Dictionary<string, string[]> { ["to[0] must be a valid email address."] = ["to[0] must be a valid email address."] },
            });
        }

        var correo = new clsCorreoFalso { Id = Guid.NewGuid(), Orden = _correos.Count, Estado = 1 };
        if (request.Content is MultipartFormDataContent formulario)
        {
            foreach (var parte in formulario)
            {
                var nombre = parte.Headers.ContentDisposition?.Name?.Trim('"') ?? string.Empty;
                if (parte.Headers.ContentDisposition?.FileName is { } archivo)
                {
                    correo.Adjuntos.Add(new clsAdjuntoFalso(archivo.Trim('"'), parte.Headers.ContentType?.MediaType, await parte.ReadAsByteArrayAsync()));
                    continue;
                }

                var valor = await parte.ReadAsStringAsync();
                switch (nombre)
                {
                    case "From": correo.From = valor; break;
                    case "To": correo.To.Add(valor); break;
                    case "Cc": correo.Cc.Add(valor); break;
                    case "Bcc": correo.Bcc.Add(valor); break;
                    case "Subject": correo.Subject = valor; break;
                    case "Body": correo.Body = valor; break;
                    case "IsHtml": correo.IsHtml = bool.Parse(valor); break;
                    case "Priority": correo.Priority = valor; break;
                    case "Metadata": correo.Metadata = valor; break;
                }
            }
        }

        correo.CorrelationId = request.Headers.TryGetValues("X-Correlation-ID", out var c) ? c.First() : null;
        _correos[correo.Id] = correo;
        return Json(HttpStatusCode.Accepted, new { id = correo.Id, status = 1, statusUrl = $"{UrlBase}/api/v1/emails/{correo.Id}" });
    }

    private static object Estado(clsCorreoFalso correo)
    {
        lock (correo)
        {
            return new
            {
                id = correo.Id,
                from = correo.From,
                subject = correo.Subject,
                isHtml = correo.IsHtml,
                priority = 1,
                status = correo.Estado,
                metadata = correo.Metadata,
                lastError = correo.UltimoError,
                attemptCount = correo.Intentos,
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow,
                sentAt = correo.EnviadoAt,
                correlationId = correo.CorrelationId,
            };
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, object cuerpo) =>
        new(estado) { Content = new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json") };
}

/// <summary>Un correo recibido por <see cref="clsMailSenderFalso"/>: los campos del formulario y su estado.</summary>
public sealed class clsCorreoFalso
{
    public Guid Id { get; init; }
    public int Orden { get; init; }
    public string? From { get; set; }
    public List<string> To { get; } = [];
    public List<string> Cc { get; } = [];
    public List<string> Bcc { get; } = [];
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public bool IsHtml { get; set; }
    public string? Priority { get; set; }
    public string? Metadata { get; set; }
    public string? CorrelationId { get; set; }
    public List<clsAdjuntoFalso> Adjuntos { get; } = [];
    public int Estado { get; set; }
    public int Intentos { get; set; }
    public string? UltimoError { get; set; }
    public DateTimeOffset? EnviadoAt { get; set; }
}

/// <summary>Adjunto recibido por <see cref="clsMailSenderFalso"/>.</summary>
public sealed record clsAdjuntoFalso(string Nombre, string? Tipo, byte[] Contenido);
