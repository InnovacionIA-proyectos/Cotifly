namespace Cotifly.Aplicacion.Margen.Exceptions;

/// <summary>
/// La fuente de margen no se pudo leer (<c>FuenteMargenNoDisponible</c> del Python). El resolver usa entonces la política
/// de la línea (decisión de #13; el Python respondía el error) y solo responde <c>MARGEN_FUENTE_NO_DISPONIBLE</c> si la
/// línea no tiene política.
/// </summary>
public sealed class clsFuenteMargenNoDisponibleException : Exception
{
    public clsFuenteMargenNoDisponibleException()
        : base("La fuente de margen no está disponible.")
    {
    }

    public clsFuenteMargenNoDisponibleException(string message)
        : base(message)
    {
    }

    public clsFuenteMargenNoDisponibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
