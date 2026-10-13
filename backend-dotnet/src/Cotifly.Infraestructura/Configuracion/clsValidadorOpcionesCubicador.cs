using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de <see cref="clsOpcionesCubicador"/> (HU-021.5, #133) en todos los entornos: con <c>Cubicador:UrlBase</c>, debe ser una URL
/// http o https absoluta sin usuario, consulta ni fragmento, y <c>Cubicador:ApiKey</c> es obligatoria (un cubicador configurado a medias
/// fallaría en cada captura). La fortaleza de la llave en Production la revisa <see cref="clsValidadorSecretosOpciones{TOpciones}"/>.
/// Los mensajes nombran la clave, nunca el valor de la llave.
/// </summary>
public sealed class clsValidadorOpcionesCubicador : IValidateOptions<clsOpcionesCubicador>
{
    private const string Prefijo = clsOpcionesCubicador.Seccion + ":";

    public ValidateOptionsResult Validate(string? name, clsOpcionesCubicador options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.UrlBase))
        {
            return ValidateOptionsResult.Success;
        }

        var errores = new List<string>();
        if (!Uri.TryCreate(options.UrlBase.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            errores.Add($"'{Prefijo}UrlBase' debe ser una URL http o https absoluta.");
        }
        else if (url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            errores.Add($"'{Prefijo}UrlBase' es la raíz del API del cubicador, sin usuario, consulta ni fragmento (la llave va en {Prefijo}ApiKey).");
        }

        if (!string.IsNullOrWhiteSpace(options.RutaSalud)
            && (!options.RutaSalud.Trim().StartsWith('/') || options.RutaSalud.Contains("://", StringComparison.Ordinal)
                || options.RutaSalud.Contains('?') || options.RutaSalud.Contains('#')))
        {
            errores.Add($"'{Prefijo}RutaSalud' es una ruta relativa a {Prefijo}UrlBase que empieza por '/' (ej. /health), sin consulta ni fragmento.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errores.Add($"'{Prefijo}ApiKey' es obligatoria con {Prefijo}UrlBase (viaja como Authorization: Bearer).");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
