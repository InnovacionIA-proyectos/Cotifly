namespace Cotifly.Aplicacion.Agente.Exceptions;

/// <summary>El loop del agente no cerró el turno dentro de <c>IA:MaxIteraciones</c> (port de <c>MaxIteracionesExcedidas</c>).</summary>
public sealed class clsMaxIteracionesExcedidasException : Exception
{
    public clsMaxIteracionesExcedidasException()
    {
    }

    public clsMaxIteracionesExcedidasException(string message)
        : base(message)
    {
    }

    public clsMaxIteracionesExcedidasException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static clsMaxIteracionesExcedidasException Con(int maximo) => new($"El agente no cerró el turno en {maximo} iteraciones.");
}

/// <summary>
/// Todos los modelos (el principal y los de <c>IA:Fallbacks</c>) fallaron por indisponibilidad (404 de ruta o
/// modelo, 408, 5xx, red o tiempo agotado). El canal responde <c>clsTextosAgente.IaNoDisponible</c> con el código
/// <c>IA_NO_DISPONIBLE</c>. Un 429 de todos no es esta excepción: se responde el texto del límite de peticiones.
/// </summary>
public sealed class clsIaNoDisponibleException : Exception
{
    public clsIaNoDisponibleException()
        : base("Ningún proveedor de IA está disponible.")
    {
    }

    public clsIaNoDisponibleException(string message)
        : base(message)
    {
    }

    public clsIaNoDisponibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Otro turno de la misma sesión sigue en curso (<c>IClsCandadoSesion</c>): el canal responde el error controlado.</summary>
public sealed class clsTurnoEnCursoException : Exception
{
    public clsTurnoEnCursoException()
        : base("Hay otro turno en curso en esta sesión.")
    {
    }

    public clsTurnoEnCursoException(string message)
        : base(message)
    {
    }

    public clsTurnoEnCursoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
