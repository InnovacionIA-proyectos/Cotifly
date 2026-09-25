using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de <see cref="clsOpcionesSapTm"/> que dependen del modo (#13). Se aplican en todos los entornos: una fuente
/// configurada a medias fallaría en cada cotización (y siempre caería a la política), mejor que no arranque.
/// La fortaleza de la clave en Production la revisa <see cref="clsValidadorSecretosOpciones{TOpciones}"/>
/// (<c>[clsSecreto]</c> sobre <c>Pass</c>). Los mensajes nombran la clave, nunca el valor de usuario o clave.
/// </summary>
public sealed class clsValidadorOpcionesSapTm : IValidateOptions<clsOpcionesSapTm>
{
    private const string Prefijo = clsOpcionesSapTm.Seccion + ":";

    public ValidateOptionsResult Validate(string? name, clsOpcionesSapTm options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errores = options.ModoNormalizado switch
        {
            "" => [],
            clsOpcionesSapTm.ModoExportJson => ValidarExport(options),
            clsOpcionesSapTm.ModoOData => ValidarOData(options),
            _ => [$"'{Prefijo}Modo' no es válido: use '{clsOpcionesSapTm.ModoExportJson}', '{clsOpcionesSapTm.ModoOData}' o déjelo vacío (fuente no configurada)."],
        };

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    private static List<string> ValidarExport(clsOpcionesSapTm options) =>
        string.IsNullOrWhiteSpace(options.ExportPath)
            ? [$"'{Prefijo}ExportPath' es obligatorio con {Prefijo}Modo={clsOpcionesSapTm.ModoExportJson}."]
            : [];

    private static List<string> ValidarOData(clsOpcionesSapTm options)
    {
        var errores = new List<string>();
        var modo = $"con {Prefijo}Modo={clsOpcionesSapTm.ModoOData}";

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            errores.Add($"'{Prefijo}Url' es obligatoria {modo}.");
        }
        else if (!Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var url)
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
            errores.Add($"'{Prefijo}Url' es la URL base del servicio, sin consulta ni fragmento (se agrega /RentabClienteSet?$filter=...).");
        }

        if (string.IsNullOrWhiteSpace(options.User))
        {
            errores.Add($"'{Prefijo}User' es obligatorio {modo}.");
        }

        if (string.IsNullOrWhiteSpace(options.Pass))
        {
            errores.Add($"'{Prefijo}Pass' es obligatoria {modo}.");
        }

        if (!string.IsNullOrWhiteSpace(options.SapClient) && !EsMandante(options.SapClient.Trim()))
        {
            errores.Add($"'{Prefijo}SapClient' debe tener exactamente 3 dígitos (ej. 100) o quedar vacío.");
        }

        return errores;
    }

    private static bool EsMandante(string valor) => valor.Length == 3 && valor.All(char.IsAsciiDigit);
}
