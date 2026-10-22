using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Dominio.Correo;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Cotifly.Infraestructura.Correo.Services;

/// <summary>
/// HU-033 (#199, ADR-023): transporte <c>mailsender</c>, cliente HTTP tipado del microservicio interno MailSender (código en
/// <c>InnovacionIA/MailSender</c>, sin autenticación: red interna):
/// <list type="bullet">
/// <item><c>POST /api/v1/emails</c> (multipart/form-data): <c>From</c>, <c>To[]</c>, <c>Cc[]</c>, <c>Bcc[]</c>, <c>Subject</c>, <c>Body</c>
/// (HTML), <c>IsHtml=true</c>, <c>Priority</c>, <c>Metadata</c> (JSON) y <c>Attachments[]</c> (el PDF). 202 <c>{id, status, statusUrl}</c>;
/// 400/413/422 con <c>ProblemDetails</c>. Header <c>X-Correlation-ID</c> = <c>{folio}:{id del correo en Cotifly}</c>.</item>
/// <item><c>GET /api/v1/emails/{id}</c>: <c>status</c> numérico (0 <c>Draft</c>, 1 <c>Queued</c>, 2 <c>Processing</c>, 3 <c>Sent</c>,
/// 4 <c>RetryScheduled</c>, 5 <c>Failed</c>, 6 <c>Cancelled</c>), <c>lastError</c>, <c>attemptCount</c> y <c>sentAt</c>; 404 si no existe.</item>
/// <item><c>POST /api/v1/emails/{id}/retry</c>: 202, o 404 si no existe.</item>
/// </list>
/// Resiliencia (<see cref="ConfigurarResiliencia"/>): tiempo total <c>Correo:MailSender:TimeoutSegundos</c>, reintentos solo en el GET
/// (MailSender no tiene clave de idempotencia: reintentar el POST podría duplicar el correo) y circuito. Nunca lanza otra cosa que
/// <see cref="clsEnvioCorreoException"/> (la cancelación del llamador se propaga). Los logs no llevan el cuerpo ni los destinatarios.
/// </summary>
public sealed class clsTransporteMailSender(HttpClient http, IOptions<clsOpcionesCorreoTransporte> opciones, ILogger<clsTransporteMailSender> logger)
    : IClsTransporteCorreo, IClsSeguimientoCorreoExterno
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "MailSender";

    public const string RutaCorreos = "api/v1/emails";
    public const string HeaderCorrelacion = "X-Correlation-ID";

    public const string MensajeNoDisponible = "MailSender no está disponible en este momento: el correo no se envió; intenta de nuevo más tarde.";

    public const string MensajeTiempoAgotado =
        "MailSender no respondió a tiempo: el correo pudo quedar encolado. Revisa el historial de envíos (o el buzón) antes de volver a enviarlo.";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public string Modo => clsOpcionesCorreoTransporte.ModoMailSender;

    public async Task<clsEnvioAceptado> EnviarAsync(clsMensajeCorreo mensaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mensaje);
        var config = opciones.Value.MailSender;
        using var formulario = Formulario(mensaje, config);
        using var peticion = new HttpRequestMessage(HttpMethod.Post, RutaCorreos) { Content = formulario };
        Correlacion(peticion, mensaje.IdCorrelacion);

        using var respuesta = await EnviarHttpAsync(peticion, "encolar", cancellationToken);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(CancellationToken.None);
        if (respuesta.StatusCode == HttpStatusCode.Accepted)
        {
            var id = LeerId(cuerpo);
            if (id is null)
            {
                logger.LogWarning("MailSender respondió 202 sin un id válido.");
                throw new clsEnvioCorreoException("MailSender aceptó el correo pero no devolvió su id: revisa el buzón antes de volver a enviarlo.")
                {
                    Incierto = true,
                };
            }

            return new clsEnvioAceptado(id, EstadoCorreo.Encolado, null);
        }

        var detalle = DetalleProblema(cuerpo);
        logger.LogWarning("MailSender rechazó el correo con HTTP {Estado}: {Detalle}", (int)respuesta.StatusCode, detalle);
        throw new clsEnvioCorreoException(respuesta.StatusCode switch
        {
            HttpStatusCode.RequestEntityTooLarge => $"MailSender rechazó el correo: el adjunto supera el tamaño permitido (HTTP 413). {detalle}".TrimEnd(),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                $"MailSender rechazó el correo por datos inválidos (HTTP {(int)respuesta.StatusCode}). {detalle}".TrimEnd(),
            _ => $"{MensajeNoDisponible} (HTTP {(int)respuesta.StatusCode})",
        });
    }

    public async Task<clsEstadoEntregaCorreo?> ConsultarAsync(Guid idExterno, string? idCorrelacion, CancellationToken cancellationToken = default)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, $"{RutaCorreos}/{idExterno:D}");
        Correlacion(peticion, idCorrelacion);
        using var respuesta = await EnviarHttpAsync(peticion, "consultar", cancellationToken);
        if (respuesta.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var cuerpo = await respuesta.Content.ReadAsStringAsync(CancellationToken.None);
        if (!respuesta.IsSuccessStatusCode)
        {
            throw new clsEnvioCorreoException($"MailSender no respondió el estado del correo (HTTP {(int)respuesta.StatusCode}).");
        }

        return LeerEstado(cuerpo) ?? throw new clsEnvioCorreoException("MailSender respondió el estado del correo con un formato inesperado.");
    }

    public async Task<bool> ReintentarAsync(Guid idExterno, string? idCorrelacion, CancellationToken cancellationToken = default)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, $"{RutaCorreos}/{idExterno:D}/retry");
        Correlacion(peticion, idCorrelacion);
        using var respuesta = await EnviarHttpAsync(peticion, "reintentar", cancellationToken);
        return respuesta.StatusCode switch
        {
            HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NoContent => true,
            HttpStatusCode.NotFound => false,
            _ => throw new clsEnvioCorreoException($"{MensajeNoDisponible} (HTTP {(int)respuesta.StatusCode})"),
        };
    }

    /// <summary>
    /// Tubería de resiliencia: tiempo total y por intento <c>Correo:MailSender:TimeoutSegundos</c>, 2 reintentos solo en métodos seguros (el
    /// GET del estado) y circuito con la muestra del doble del tiempo total.
    /// </summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia, TimeSpan tiempo)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.TotalRequestTimeout.Timeout = tiempo;
        resiliencia.AttemptTimeout.Timeout = tiempo;
        resiliencia.Retry.MaxRetryAttempts = 2;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(300);
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        resiliencia.Retry.DisableForUnsafeHttpMethods();
        resiliencia.CircuitBreaker.SamplingDuration = tiempo * 2 > TimeSpan.FromSeconds(30) ? tiempo * 2 : TimeSpan.FromSeconds(30);
    }

    /// <summary>Estado de MailSender (<c>EmailMessageStatus</c>) → estado de Cotifly.</summary>
    public static EstadoCorreo Traducir(int estado) => estado switch
    {
        3 => EstadoCorreo.Enviado,
        2 or 4 => EstadoCorreo.Enviando,
        5 or 6 => EstadoCorreo.Fallido,
        _ => EstadoCorreo.Encolado, // 0 Draft, 1 Queued (y uno desconocido: se sigue consultando)
    };

    /// <summary>El formulario multipart de <c>CreateEmailForm</c> (un campo por destinatario).</summary>
    internal static MultipartFormDataContent Formulario(clsMensajeCorreo mensaje, clsOpcionesMailSender config)
    {
        var remitente = string.IsNullOrWhiteSpace(mensaje.Remitente) ? config.Remitente : mensaje.Remitente.Trim();
        var formulario = new MultipartFormDataContent
        {
            { Texto(remitente), "From" },
            { Texto(mensaje.Asunto), "Subject" },
            { Texto(mensaje.CuerpoHtml), "Body" },
            { Texto("true"), "IsHtml" },
            { Texto(config.Prioridad), "Priority" },
        };
        foreach (var para in mensaje.Para)
        {
            formulario.Add(Texto(para), "To");
        }

        foreach (var cc in mensaje.Cc)
        {
            formulario.Add(Texto(cc), "Cc");
        }

        foreach (var bcc in mensaje.Bcc)
        {
            formulario.Add(Texto(bcc), "Bcc");
        }

        if (mensaje.Metadatos.Count > 0)
        {
            formulario.Add(Texto(JsonSerializer.Serialize(mensaje.Metadatos, _json)), "Metadata");
        }

        var adjunto = new ByteArrayContent(mensaje.Adjunto.Contenido);
        adjunto.Headers.ContentType = MediaTypeHeaderValue.Parse(mensaje.Adjunto.TipoMime);
        formulario.Add(adjunto, "Attachments", mensaje.Adjunto.Nombre);
        return formulario;
    }

    private static StringContent Texto(string valor) => new(valor, Encoding.UTF8);

    private static void Correlacion(HttpRequestMessage peticion, string? idCorrelacion)
    {
        if (!string.IsNullOrWhiteSpace(idCorrelacion))
        {
            peticion.Headers.TryAddWithoutValidation(HeaderCorrelacion, idCorrelacion);
        }
    }

    private async Task<HttpResponseMessage> EnviarHttpAsync(HttpRequestMessage peticion, string operacion, CancellationToken cancellationToken)
    {
        if (http.BaseAddress is null)
        {
            throw new clsEnvioCorreoException("MailSender no está configurado (Correo:MailSender:UrlBase).");
        }

        try
        {
            return await http.SendAsync(peticion, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning("MailSender no respondió a tiempo al {Operacion}.", operacion);
            throw new clsEnvioCorreoException(MensajeTiempoAgotado, ex) { Incierto = peticion.Method == HttpMethod.Post };
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException)
        {
            logger.LogWarning("MailSender no está disponible al {Operacion}: {Tipo}", operacion, ex.GetType().Name);
            throw new clsEnvioCorreoException(MensajeNoDisponible, ex);
        }
    }

    private static Guid? LeerId(string cuerpo)
    {
        try
        {
            using var documento = JsonDocument.Parse(cuerpo);
            return documento.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var guid)
                ? guid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>EmailStatusResponse</c> → estado de Cotifly; <c>null</c> si el JSON no tiene la forma esperada.</summary>
    internal static clsEstadoEntregaCorreo? LeerEstado(string cuerpo)
    {
        try
        {
            using var documento = JsonDocument.Parse(cuerpo);
            var raiz = documento.RootElement;
            if (!raiz.TryGetProperty("status", out var status))
            {
                return null;
            }

            int? codigo = status.ValueKind switch
            {
                JsonValueKind.Number when status.TryGetInt32(out var n) => n,
                JsonValueKind.String => status.GetString() switch
                {
                    "Draft" => 0,
                    "Queued" => 1,
                    "Processing" => 2,
                    "Sent" => 3,
                    "RetryScheduled" => 4,
                    "Failed" => 5,
                    "Cancelled" => 6,
                    var texto when int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                    _ => null,
                },
                _ => null,
            };
            if (codigo is null)
            {
                return null;
            }

            var estado = Traducir(codigo.Value);
            var error = raiz.TryGetProperty("lastError", out var ultimo) && ultimo.ValueKind == JsonValueKind.String ? ultimo.GetString() : null;
            if (estado == EstadoCorreo.Fallido && string.IsNullOrWhiteSpace(error))
            {
                error = codigo == 6 ? "MailSender canceló el envío." : "MailSender no pudo entregar el correo.";
            }

            int? intentos = raiz.TryGetProperty("attemptCount", out var conteo) && conteo.TryGetInt32(out var c) ? c : null;
            DateTimeOffset? enviado = raiz.TryGetProperty("sentAt", out var sentAt) && sentAt.ValueKind == JsonValueKind.String
                                      && sentAt.TryGetDateTimeOffset(out var fecha) ? fecha : null;
            return new clsEstadoEntregaCorreo(estado, estado == EstadoCorreo.Enviado ? null : error, intentos, enviado);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Resumen de un <c>ProblemDetails</c>/<c>ValidationProblemDetails</c> de MailSender (título y errores, recortado).</summary>
    internal static string DetalleProblema(string cuerpo)
    {
        try
        {
            using var documento = JsonDocument.Parse(cuerpo);
            var raiz = documento.RootElement;
            var partes = new List<string>();
            if (raiz.TryGetProperty("title", out var titulo) && titulo.ValueKind == JsonValueKind.String)
            {
                partes.Add(titulo.GetString()!);
            }

            if (raiz.TryGetProperty("detail", out var detalle) && detalle.ValueKind == JsonValueKind.String)
            {
                partes.Add(detalle.GetString()!);
            }

            if (raiz.TryGetProperty("errors", out var errores) && errores.ValueKind == JsonValueKind.Object)
            {
                partes.AddRange(errores.EnumerateObject().Select(e => e.Name).Take(5));
            }

            var texto = string.Join(" ", partes);
            return texto.Length <= 500 ? texto : texto[..500];
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
