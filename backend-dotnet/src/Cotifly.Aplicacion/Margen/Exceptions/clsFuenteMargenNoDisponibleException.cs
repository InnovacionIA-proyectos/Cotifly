namespace Cotifly.Aplicacion.Margen.Exceptions;

/// <summary>
/// La fuente de margen no se pudo leer. El resolver usa entonces la política de la línea: solo se consulta SAP con política,
/// así que la caída nunca llega al cliente como error.
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
