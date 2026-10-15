using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Reglas de <see cref="clsOpcionesBaseDatos"/> (HU-023 #176): <c>BaseDatos:MotivoBusquedaAgotada</c> debe ser uno de
/// <see cref="clsOpcionesBaseDatos.Modos"/>. Con otro valor la API no arranca.
/// </summary>
public sealed class clsValidadorOpcionesBaseDatos : IValidateOptions<clsOpcionesBaseDatos>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesBaseDatos options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return clsOpcionesBaseDatos.Modos.Contains(options.MotivoBusquedaAgotadaNormalizado)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{clsOpcionesBaseDatos.Seccion}:MotivoBusquedaAgotada' debe ser {string.Join(", ", clsOpcionesBaseDatos.Modos)} "
                + $"(llegó '{options.MotivoBusquedaAgotada}').");
    }
}
