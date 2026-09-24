namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Regla genérica para valores secretos de configuración. Solo se aplica en Production:
/// en Development y en pruebas se aceptan valores de ejemplo.
/// Los mensajes nombran la clave de configuración pero nunca incluyen el valor.
/// </summary>
public static class clsValidadorSecretos
{
    /// <summary>Valores que, completos, se consideran placeholder (sin distinguir mayúsculas).</summary>
    private static readonly string[] _placeholdersExactos = ["dev", "test", "secret", "secreto", "password", "clave", "xxx"];

    /// <summary>Fragmentos que delatan un valor de ejemplo sin reemplazar (sin distinguir mayúsculas).</summary>
    private static readonly string[] _fragmentosPlaceholder = ["cambiar", "changeme", "change-me", "change_me", "reemplazar"];

    /// <summary>
    /// Valida un secreto. Devuelve el mensaje de error o <c>null</c> si es aceptable.
    /// </summary>
    /// <param name="clave">Clave de configuración (ej. <c>Jwt:SigningKey</c>); aparece en el mensaje.</param>
    /// <param name="valor">Valor leído; nunca aparece en el mensaje.</param>
    /// <param name="largoMinimo">Largo mínimo aceptado.</param>
    /// <param name="esProduccion">Solo se valida si es <c>true</c>.</param>
    public static string? Validar(string clave, string? valor, int largoMinimo, bool esProduccion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clave);
        ArgumentOutOfRangeException.ThrowIfNegative(largoMinimo);

        if (!esProduccion)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(valor))
        {
            return $"El secreto '{clave}' está vacío. Configúrelo en el despliegue (variable {AVariable(clave)}).";
        }

        if (EsPlaceholder(valor.Trim()))
        {
            return $"El secreto '{clave}' tiene un valor placeholder de ejemplo. Reemplácelo en el despliegue (variable {AVariable(clave)}).";
        }

        if (valor.Length < largoMinimo)
        {
            return $"El secreto '{clave}' es demasiado corto: se exigen al menos {largoMinimo} caracteres.";
        }

        return null;
    }

    private static bool EsPlaceholder(string valor) =>
        (valor.StartsWith('<') && valor.EndsWith('>'))
        || _placeholdersExactos.Any(p => valor.Equals(p, StringComparison.OrdinalIgnoreCase))
        || _fragmentosPlaceholder.Any(f => valor.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static string AVariable(string clave) => clave.Replace(":", "__", StringComparison.Ordinal);
}
