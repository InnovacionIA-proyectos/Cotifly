using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// App de Google Chat (sección <c>GChat</c>, port de <c>gchat_*</c> y <c>google_sa_json</c> de <c>shared/config.py</c>). Variables
/// <c>GChat__ProjectNumber</c>, <c>GChat__VerificarJwt</c>, <c>GChat__SaJson</c>, <c>GChat__SupportSpace</c>...
/// </summary>
public sealed class clsOpcionesGoogleChat
{
    public const string Seccion = "GChat";
    public const string UrlCertificadosPorDefecto = "https://www.googleapis.com/service_accounts/v1/metadata/x509/chat@system.gserviceaccount.com";

    /// <summary>
    /// Número del proyecto de Google Cloud de la app de Chat: audiencia del JWT del webhook. Sin él, con la verificación activa,
    /// todo evento responde 401 (fail-closed, como el Python).
    /// </summary>
    [MaxLength(30)]
    public string? ProjectNumber { get; set; }

    /// <summary>Verifica el JWT de Google en <c>POST /chat/events</c>. Por defecto <c>true</c>; <c>false</c> solo para pruebas con túnel.</summary>
    public bool VerificarJwt { get; set; } = true;

    /// <summary>
    /// Cuenta de servicio de la app (secreto): el contenido JSON del archivo de claves o la ruta al archivo (secreto de Docker,
    /// como <c>google_sa_json</c> del Python). Con ella se publica en Chat (<c>spaces.messages.create</c>, alcance <c>chat.bot</c>).
    /// Sin ella las respuestas no se publican (queda un warning).
    /// </summary>
    [clsSecreto(LargoMinimo = 10, Opcional = true)]
    public string? SaJson { get; set; }

    /// <summary>Contenido JSON de la cuenta de servicio: <see cref="SaJson"/> si empieza por <c>{</c>, o el archivo al que apunta.</summary>
    public string? LeerCuentaServicio()
    {
        if (string.IsNullOrWhiteSpace(SaJson))
        {
            return null;
        }

        var valor = SaJson.Trim();
        return valor.StartsWith('{') ? valor : File.ReadAllText(valor);
    }

    /// <summary>Space de soporte (<c>spaces/...</c>) donde se publican las alertas de error del worker. Vacío = sin alertas.</summary>
    [MaxLength(200)]
    public string? SupportSpace { get; set; }

    /// <summary>Certificados públicos de <c>chat@system.gserviceaccount.com</c> (formato x509 por <c>kid</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(500)]
    public string UrlCertificados { get; set; } = UrlCertificadosPorDefecto;

    /// <summary>Base de la API REST de Chat.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(300)]
    public string ApiBase { get; set; } = "https://chat.googleapis.com/v1";

    /// <summary>Eventos que caben en la cola del webhook; si se llena, el evento se acusa pero no se procesa. Por defecto 200.</summary>
    [Range(1, 100_000)]
    public int CapacidadCola { get; set; } = 200;
}

/// <summary>Valida <see cref="clsOpcionesGoogleChat"/>: número de proyecto numérico, URLs absolutas y cuenta de servicio legible.</summary>
public sealed class clsValidadorOpcionesGoogleChat : IValidateOptions<clsOpcionesGoogleChat>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesGoogleChat options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ProjectNumber) && !options.ProjectNumber.Trim().All(char.IsAsciiDigit))
        {
            errores.Add("GChat:ProjectNumber debe ser el número (solo dígitos) del proyecto de Google Cloud de la app de Chat.");
        }

        foreach (var (clave, valor) in new[] { ("GChat:UrlCertificados", options.UrlCertificados), ("GChat:ApiBase", options.ApiBase) })
        {
            if (!Uri.TryCreate(valor?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                errores.Add($"{clave} debe ser una URL http(s) absoluta.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.SaJson) && !CuentaServicioLegible(options))
        {
            errores.Add("GChat:SaJson debe ser el JSON de la cuenta de servicio (con client_email y private_key) o la ruta a un archivo legible con ese JSON.");
        }

        if (!string.IsNullOrWhiteSpace(options.SupportSpace) && !options.SupportSpace.Trim().StartsWith("spaces/", StringComparison.Ordinal))
        {
            errores.Add("GChat:SupportSpace debe tener la forma spaces/XXXX.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    private static bool CuentaServicioLegible(clsOpcionesGoogleChat options)
    {
        try
        {
            using var documento = JsonDocument.Parse(options.LeerCuentaServicio() ?? string.Empty);
            var raiz = documento.RootElement;
            return raiz.ValueKind == JsonValueKind.Object
                   && raiz.TryGetProperty("client_email", out var correo) && correo.ValueKind == JsonValueKind.String
                   && raiz.TryGetProperty("private_key", out var clave) && clave.ValueKind == JsonValueKind.String;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Firma de la URL del PDF para Google Chat (sección <c>Pdf</c>, ADR-011 §3). Variables <c>Pdf__ClaveFirmaUrl</c> y
/// <c>Pdf__MinutosUrlFirmada</c>.
/// </summary>
public sealed class clsOpcionesFirmaUrlPdf
{
    public const string Seccion = "Pdf";

    /// <summary>
    /// Clave HMAC (secreto, al menos 32 caracteres). Vacía: clave aleatoria por proceso (los enlaces dejan de valer al reiniciar y
    /// no sirven entre réplicas; queda un warning). Con varias réplicas todas deben compartirla.
    /// </summary>
    [clsSecreto(LargoMinimo = 32, Opcional = true)]
    public string? ClaveFirmaUrl { get; set; }

    /// <summary>Vigencia del enlace firmado en minutos. Por defecto 15.</summary>
    [Range(1, 120)]
    public int MinutosUrlFirmada { get; set; } = 15;
}
