namespace Cotifly.Aplicacion.Margen.Exceptions;

/// <summary>
/// La fuente de margen no se pudo leer (<c>FuenteMargenNoDisponible</c> del Python). El resolver la traduce a
/// <c>MARGEN_FUENTE_NO_DISPONIBLE</c> sin caer a la política, igual que el Python.
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
