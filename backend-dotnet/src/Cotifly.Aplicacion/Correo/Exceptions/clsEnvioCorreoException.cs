namespace Cotifly.Aplicacion.Correo.Exceptions;

/// <summary>
/// Fallo del transporte de correo: MailSender rechazó el mensaje (400/413/422), no respondió a tiempo o no está disponible.
/// <c>clsCorreoService</c> la traduce a <c>CORREO_ENVIO_FALLIDO</c> y registra el envío como <c>fallido</c>. El mensaje no lleva
/// credenciales ni el contenido del correo.
/// </summary>
public sealed class clsEnvioCorreoException : Exception
{
    public clsEnvioCorreoException()
        : base("Falló el envío del correo.")
    {
    }

    public clsEnvioCorreoException(string message)
        : base(message)
    {
    }

    public clsEnvioCorreoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// HU-033: el transporte no respondió a tiempo después de recibir el mensaje, así que pudo quedar encolado. El comercial debe revisar
    /// el buzón o el historial antes de volver a enviar.
    /// </summary>
    public bool Incierto { get; init; }
}
