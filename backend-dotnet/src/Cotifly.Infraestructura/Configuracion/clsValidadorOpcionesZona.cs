using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>Verifica que <c>Zona:ZonaHoraria</c> exista en la base de zonas del sistema.</summary>
public sealed class clsValidadorOpcionesZona : IValidateOptions<clsOpcionesZona>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesZona options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var clave = $"{clsOpcionesZona.Seccion}:{nameof(clsOpcionesZona.ZonaHoraria)}";

        if (string.IsNullOrWhiteSpace(options.ZonaHoraria)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(options.ZonaHoraria, out _))
        {
            return ValidateOptionsResult.Fail(
                $"'{clave}' no es una zona horaria válida en este sistema (use un identificador IANA, ej. America/Bogota).");
        }

        return ValidateOptionsResult.Success;
    }
}
