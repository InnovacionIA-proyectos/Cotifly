namespace Cotifly.Aplicacion.Correo.Exceptions;

/// <summary>
/// Fallo del transporte de correo (<c>EnvioSmtpError</c> del Python): red, relay que rechaza los destinatarios, timeout.
/// <c>clsCorreoService</c> la traduce a <c>CORREO_ENVIO_FALLIDO</c> y registra el envío como <c>fallido</c>. El mensaje no
/// lleva credenciales.
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
}
