using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de <see cref="clsOpcionesConexiones"/> que DataAnnotations no cubre (ADR-008). Solo en Production:
/// la cadena debe poder leerse, no puede usar autenticación integrada (<c>Trusted_Connection</c> /
/// <c>Integrated Security</c>) y la contraseña no puede ser un placeholder. Los mensajes nombran la
/// clave, nunca el valor.
/// </summary>
public sealed class clsValidadorOpcionesConexiones(IHostEnvironment entorno) : IValidateOptions<clsOpcionesConexiones>
{
    private static readonly string[] _clavesIntegrada = ["Trusted_Connection", "Integrated Security"];
    private static readonly string[] _clavesPassword = ["Password", "Pwd"];

    public ValidateOptionsResult Validate(string? name, clsOpcionesConexiones options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!entorno.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var errores = new[]
            {
                (Clave: $"{clsOpcionesConexiones.Seccion}:{nameof(clsOpcionesConexiones.Cotifly)}", Valor: options.Cotifly),
                (Clave: $"{clsOpcionesConexiones.Seccion}:{nameof(clsOpcionesConexiones.Identidad)}", Valor: options.Identidad),
            }
            .Where(c => !string.IsNullOrWhiteSpace(c.Valor))
            .SelectMany(c => Validar(c.Clave, c.Valor!))
            .ToList();

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    private static IEnumerable<string> Validar(string clave, string cadena)
    {
        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = cadena;
        }
        catch (ArgumentException)
        {
            return [$"'{clave}' no es una cadena de conexión válida."];
        }

        var errores = new List<string>();

        if (_clavesIntegrada.Any(k => builder.TryGetValue(k, out var v) && EsVerdadero(v)))
        {
            errores.Add($"'{clave}' usa autenticación integrada de Windows: el contenedor Linux exige SQL login (ADR-008).");
        }

        var password = _clavesPassword
            .Select(k => builder.TryGetValue(k, out var v) ? v?.ToString() : null)
            .FirstOrDefault(v => v is not null);

        if (clsValidadorSecretos.Validar($"{clave} (Password)", password, 8, esProduccion: true) is { } errorPassword)
        {
            errores.Add(errorPassword);
        }

        return errores;
    }

    private static bool EsVerdadero(object? valor) =>
        valor?.ToString()?.Trim().ToUpperInvariant() is "TRUE" or "YES" or "SSPI";
}
