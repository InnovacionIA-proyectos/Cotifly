namespace Cotifly.Aplicacion.Margen.Exceptions;

/// <summary>
/// La base de Cotifly no respondió al leer el estado de la sincronización del margen de SAP TM (la última corrida, las
/// filas activas) o al registrar una corrida fallida. La administración responde 503 <c>SAP_MARGEN_NO_DISPONIBLE</c>; el worker solo lo
/// anota y reintenta en el chequeo siguiente. El mensaje no lleva datos de conexión.
/// </summary>
public sealed class clsSincronizacionMargenSapNoDisponibleException : Exception
{
    public clsSincronizacionMargenSapNoDisponibleException()
        : base("La base de Cotifly no respondió al leer o registrar la sincronización del margen de SAP TM.")
    {
    }

    public clsSincronizacionMargenSapNoDisponibleException(string message)
        : base(message)
    {
    }

    public clsSincronizacionMargenSapNoDisponibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
