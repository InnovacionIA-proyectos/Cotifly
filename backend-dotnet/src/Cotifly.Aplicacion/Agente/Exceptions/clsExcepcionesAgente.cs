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

/// <summary>La respuesta final del modelo no pasó la validación (<c>IClsValidadorRespuestaAgente</c>, HU-003.10).</summary>
public sealed class clsRespuestaAgenteInvalidaException : Exception
{
    public clsRespuestaAgenteInvalidaException()
    {
    }

    public clsRespuestaAgenteInvalidaException(string message)
        : base(message)
    {
    }

    public clsRespuestaAgenteInvalidaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
