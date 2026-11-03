using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de forma de <see cref="clsOpcionesSapTmSincronizacion"/> (HU-042.2, #243), en todos los entornos. Que falte la URL, el usuario o
/// la clave NO es un error: el worker queda inactivo. Una URL o un mandante mal escritos sí impiden arrancar (la sincronización fallaría en
/// cada corrida sin que nadie lo note). Los mensajes nombran la clave, nunca el valor de usuario o clave.
/// </summary>
public sealed class clsValidadorOpcionesSapTmSincronizacion : IValidateOptions<clsOpcionesSapTmSincronizacion>
{
    private const string Prefijo = clsOpcionesSapTmSincronizacion.Seccion + ":";

    public ValidateOptionsResult Validate(string? name, clsOpcionesSapTmSincronizacion options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.Url))
        {
            if (!Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                errores.Add($"'{Prefijo}Url' debe ser una URL http o https absoluta.");
            }
            else if (url.UserInfo.Length > 0)
            {
                errores.Add($"'{Prefijo}Url' no puede llevar usuario ni clave: use {Prefijo}User y {Prefijo}Pass.");
            }
            else if (url.Query.Length > 0 || url.Fragment.Length > 0)
            {
                errores.Add($"'{Prefijo}Url' es la URL del servicio, sin consulta ni fragmento (se agrega /RentabClienteSet?$format=json).");
            }
            else if (url.AbsolutePath.TrimEnd('/').EndsWith("/RentabClienteSet", StringComparison.OrdinalIgnoreCase))
            {
                errores.Add($"'{Prefijo}Url' es la URL del servicio, sin /RentabClienteSet (se agrega sola).");
            }
        }

        if (options.SapClientNormalizado is { } mandante && !(mandante.Length == 3 && mandante.All(char.IsAsciiDigit)))
        {
            errores.Add($"'{Prefijo}SapClient' debe tener exactamente 3 dígitos (ej. 100) o quedar vacío.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
