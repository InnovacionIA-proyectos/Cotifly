using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Transporte del correo (sección <c>Correo</c>, la misma de <c>clsOpcionesCorreo</c> de Aplicación). Port de
/// <c>correo_modo</c> y <c>smtp_relay_*</c> de <c>shared/config.py</c>.
/// </summary>
/// <remarks>
/// Solo <c>smtp_relay</c> está implementado (relay interno de Botero Soto, Microsoft ESMTP sin AUTH ni STARTTLS, control
/// por red/IP). <c>wscorreos</c> (SOAP) y <c>smtp</c> (SMTP con auth/TLS) no se portan hasta que el equipo confirme cuál es
/// el transporte real de producción (D3, checklist de #11); con <c>Habilitado=true</c> y uno de esos modos la API no arranca.
/// </remarks>
public sealed class clsOpcionesCorreoTransporte
{
    public const string Seccion = "Correo";
    public const string ModoSmtpRelay = "smtp_relay";
    public const string ModoWsCorreos = "wscorreos";
    public const string ModoSmtp = "smtp";

    /// <summary>Mismo valor que <c>clsOpcionesCorreo.Habilitado</c>: las reglas del transporte solo aplican si está habilitado.</summary>
    public bool Habilitado { get; set; }

    /// <summary><c>smtp_relay</c> (por defecto, como el Python) | <c>wscorreos</c> | <c>smtp</c>.</summary>
    public string? Modo { get; set; } = ModoSmtpRelay;

    public clsOpcionesSmtpRelay SmtpRelay { get; set; } = new();

    /// <summary><see cref="Modo"/> sin espacios y en minúsculas; vacío equivale a <c>smtp_relay</c>.</summary>
    public string ModoNormalizado => string.IsNullOrWhiteSpace(Modo) ? ModoSmtpRelay : Modo.Trim().ToLowerInvariant();
}

/// <summary>Relay SMTP interno (<c>Correo:SmtpRelay</c>).</summary>
public sealed class clsOpcionesSmtpRelay
{
    /// <summary>Host del relay (en el Python, <c>10.10.40.62</c>). Obligatorio con el correo habilitado.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 25;

    /// <summary>Remitente cuando el comercial no tiene correo en <c>UsuariosWeb</c>.</summary>
    public string FromFallback { get; set; } = "notificaciones@boterosoto.com.co";

    /// <summary>Tiempo máximo de cada operación con el relay (el Python usaba 20 s).</summary>
    public int TimeoutSeg { get; set; } = 20;
}

/// <summary>Reglas de <see cref="clsOpcionesCorreoTransporte"/>; con el correo deshabilitado solo se revisa que el modo exista.</summary>
public sealed class clsValidadorOpcionesCorreoTransporte : IValidateOptions<clsOpcionesCorreoTransporte>
{
    private const string Prefijo = clsOpcionesCorreoTransporte.Seccion + ":";

    public ValidateOptionsResult Validate(string? name, clsOpcionesCorreoTransporte options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var modo = options.ModoNormalizado;
        if (modo is not (clsOpcionesCorreoTransporte.ModoSmtpRelay or clsOpcionesCorreoTransporte.ModoWsCorreos or clsOpcionesCorreoTransporte.ModoSmtp))
        {
            return ValidateOptionsResult.Fail($"'{Prefijo}Modo' no es válido: use '{clsOpcionesCorreoTransporte.ModoSmtpRelay}'.");
        }

        if (!options.Habilitado)
        {
            return ValidateOptionsResult.Success;
        }

        if (modo != clsOpcionesCorreoTransporte.ModoSmtpRelay)
        {
            return ValidateOptionsResult.Fail(
                $"'{Prefijo}Modo={modo}' no está implementado en el backend .NET (pendiente de confirmar el transporte real, D3): use '{clsOpcionesCorreoTransporte.ModoSmtpRelay}' o deshabilite el correo.");
        }

        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(options.SmtpRelay.Host))
        {
            errores.Add($"'{Prefijo}SmtpRelay:Host' es obligatorio con {Prefijo}Habilitado=true.");
        }

        if (string.IsNullOrWhiteSpace(options.SmtpRelay.FromFallback) || !options.SmtpRelay.FromFallback.Contains('@', StringComparison.Ordinal))
        {
            errores.Add($"'{Prefijo}SmtpRelay:FromFallback' debe ser un correo.");
        }

        // DataAnnotations no valida objetos anidados: los rangos se revisan aquí.
        if (options.SmtpRelay.Port is < 1 or > 65535)
        {
            errores.Add($"'{Prefijo}SmtpRelay:Port' debe estar entre 1 y 65535.");
        }

        if (options.SmtpRelay.TimeoutSeg is < 1 or > 300)
        {
            errores.Add($"'{Prefijo}SmtpRelay:TimeoutSeg' debe estar entre 1 y 300 segundos.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
