namespace Cotifly.Api.Comun;

/// <summary>
/// Marca una propiedad de un DTO de entrada de <c>/web/*</c> que ya NO está en <c>specs/openapi.yaml</c> pero que se sigue leyendo para
/// no romper a un frontend que todavía la manda: el valor se ignora sin error y queda un aviso en el log
/// (<see cref="clsCamposRetirados.Registrar"/>). La prueba de contrato excluye estas propiedades de la comparación exacta con el esquema
/// y exige que el esquema NO las declare.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class clsCampoRetiradoAttribute(string motivo) : Attribute
{
    /// <summary>Por qué se retiró el campo.</summary>
    public string Motivo { get; } = motivo;
}

/// <summary>Aviso en el log de los campos retirados que llegaron en una petición (nunca sus valores).</summary>
public static partial class clsCamposRetirados
{
    /// <summary>
    /// Deja una línea de warning con el endpoint y los nombres snake_case de los campos retirados que llegaron (sin sus valores: pueden
    /// traer datos del cliente). No hace nada si no llegó ninguno.
    /// </summary>
    public static void Registrar(ILogger logger, string endpoint, IReadOnlyCollection<string> campos)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(campos);
        if (campos.Count > 0)
        {
            LogCamposRetirados(logger, endpoint, string.Join(", ", campos));
        }
    }

    [LoggerMessage(EventId = 2110, EventName = "CAMPOS_RETIRADOS", Level = LogLevel.Warning,
        Message = "CAMPOS_RETIRADOS: {Endpoint} recibió {Campos}, que ya no están en el contrato (HU-021, decisión 1): se ignoran.")]
    private static partial void LogCamposRetirados(ILogger logger, string endpoint, string campos);
}
