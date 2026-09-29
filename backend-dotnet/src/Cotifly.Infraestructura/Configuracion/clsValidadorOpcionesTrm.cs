using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de <see cref="clsOpcionesTrm"/> que DataAnnotations no cubre (HU-012.1): la URL debe ser http o https absoluta, sin usuario,
/// consulta ni fragmento (el adaptador agrega <c>?$where=...</c>). Con una URL inválida la API no arranca: cada cotización en USD
/// fallaría.
/// </summary>
public sealed class clsValidadorOpcionesTrm : IValidateOptions<clsOpcionesTrm>
{
    private const string Clave = clsOpcionesTrm.Seccion + ":Url";

    public ValidateOptionsResult Validate(string? name, clsOpcionesTrm options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return ValidateOptionsResult.Fail($"'{Clave}' es obligatoria (por defecto {clsOpcionesTrm.UrlPorDefecto}).");
        }

        if (!Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail($"'{Clave}' debe ser una URL http o https absoluta.");
        }

        if (url.UserInfo.Length > 0)
        {
            return ValidateOptionsResult.Fail($"'{Clave}' no puede llevar usuario ni clave: la fuente de la TRM es pública.");
        }

        if (url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            return ValidateOptionsResult.Fail($"'{Clave}' es la URL del recurso, sin consulta ni fragmento (el adaptador agrega ?$where=...).");
        }

        return ValidateOptionsResult.Success;
    }
}
