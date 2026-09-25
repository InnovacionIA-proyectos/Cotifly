using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Tokens de sesión (sección <c>Jwt</c>, ADR-009): access JWT HS256 y refresh opaco. Variables <c>Jwt__Issuer</c>,
/// <c>Jwt__Audience</c>, <c>Jwt__SigningKey</c>, <c>Jwt__AccessMinutes</c>, <c>Jwt__RefreshDays</c>.
/// </summary>
/// <remarks>
/// <see cref="SigningKey"/> es secreto: en Production es obligatoria, sin placeholders y de al menos 32 caracteres
/// (<see cref="clsSecretoAttribute"/>), y en cualquier entorno, si tiene valor, debe tener al menos 32 bytes en UTF-8
/// (<see cref="clsValidadorOpcionesJwt"/>). Fuera de Production puede quedar vacía: se usa una clave aleatoria efímera por
/// proceso (los tokens dejan de valer al reiniciar) y se registra un warning.
/// </remarks>
public sealed class clsOpcionesJwt
{
    public const string Seccion = "Jwt";
    public const int BytesMinimosClave = 32;

    /// <summary>Claim <c>iss</c>. Por defecto <c>cotifly-api</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    public string Issuer { get; set; } = "cotifly-api";

    /// <summary>Claim <c>aud</c>. Por defecto <c>clientepro</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    public string Audience { get; set; } = "clientepro";

    /// <summary>Clave HS256 (al menos 32 bytes).</summary>
    [clsSecreto(LargoMinimo = BytesMinimosClave)]
    public string? SigningKey { get; set; }

    /// <summary>Vigencia del access token en minutos (1–1440). Por defecto 60.</summary>
    [Range(1, 1440)]
    public int AccessMinutes { get; set; } = 60;

    /// <summary>Vigencia absoluta de la sesión de refresh en días (1–365). Por defecto 30.</summary>
    [Range(1, 365)]
    public int RefreshDays { get; set; } = 30;
}

/// <summary>Una <c>Jwt:SigningKey</c> con valor debe tener al menos 32 bytes en UTF-8, en cualquier entorno (HS256).</summary>
public sealed class clsValidadorOpcionesJwt : IValidateOptions<clsOpcionesJwt>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesJwt options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(options.SigningKey) && Encoding.UTF8.GetByteCount(options.SigningKey) < clsOpcionesJwt.BytesMinimosClave)
        {
            return ValidateOptionsResult.Fail(
                $"'{clsOpcionesJwt.Seccion}:{nameof(clsOpcionesJwt.SigningKey)}' debe tener al menos {clsOpcionesJwt.BytesMinimosClave} bytes (HS256).");
        }

        return ValidateOptionsResult.Success;
    }
}
