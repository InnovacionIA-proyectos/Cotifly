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
