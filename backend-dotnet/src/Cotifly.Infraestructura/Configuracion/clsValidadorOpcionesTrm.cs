using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de <see cref="clsOpcionesTrm"/> que DataAnnotations no cubre (HU-012.1): <c>Trm:Proveedor</c> debe ser uno de
/// <see cref="clsOpcionesTrm.Proveedores"/>, y <c>Trm:Url</c> y <c>Trm:UrlDolarApi</c> deben ser URLs https absolutas, sin usuario,
/// consulta ni fragmento (a la de la Superintendencia el adaptador le agrega <c>?$where=...</c>). Con una opción inválida la API no
/// arranca: cada cotización en USD fallaría.
/// </summary>
public sealed class clsValidadorOpcionesTrm : IValidateOptions<clsOpcionesTrm>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesTrm options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!clsOpcionesTrm.Proveedores.Contains(options.ProveedorNormalizado))
        {
            return ValidateOptionsResult.Fail(
                $"'{clsOpcionesTrm.Seccion}:Proveedor' debe ser {string.Join(", ", clsOpcionesTrm.Proveedores)} (llegó '{options.Proveedor}').");
        }

        return ValidarUrl(options.Url, "Url", clsOpcionesTrm.UrlPorDefecto)
               ?? ValidarUrl(options.UrlDolarApi, "UrlDolarApi", clsOpcionesTrm.UrlDolarApiPorDefecto)
               ?? ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult? ValidarUrl(string? valor, string nombre, string porDefecto)
    {
        var clave = clsOpcionesTrm.Seccion + ":" + nombre;
        if (string.IsNullOrWhiteSpace(valor))
        {
            return ValidateOptionsResult.Fail($"'{clave}' es obligatoria (por defecto {porDefecto}).");
        }

        if (!Uri.TryCreate(valor.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return ValidateOptionsResult.Fail($"'{clave}' debe ser una URL https absoluta.");
        }

        if (url.UserInfo.Length > 0)
        {
            return ValidateOptionsResult.Fail($"'{clave}' no puede llevar usuario ni clave: la fuente de la TRM es pública.");
        }

        if (url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            return ValidateOptionsResult.Fail($"'{clave}' es la URL del recurso, sin consulta ni fragmento.");
        }

        return null;
    }
}
