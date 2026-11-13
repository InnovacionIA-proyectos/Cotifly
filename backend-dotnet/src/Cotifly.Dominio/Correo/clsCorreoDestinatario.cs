using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Correo;

/// <summary>Destinatario de un correo (<c>dbo_cotifly.CorreoDestinatarios</c>), tipo <c>para</c> o <c>cc</c>.</summary>
public sealed class clsCorreoDestinatario
{
    public const int LargoMaximoEmail = 320;

    /// <summary>Mensaje de <c>CORREO_INVALIDO</c> para una dirección con saltos de línea u otros caracteres de control.</summary>
    public const string MensajeConCaracteresDeControl =
        "Un destinatario tiene saltos de línea u otros caracteres de control: escribe una sola dirección de correo por destinatario.";

    private clsCorreoDestinatario()
    {
    }

    public Guid Id { get; private set; }
    public Guid CorreoId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public TipoDestinatario Tipo { get; private set; }

    /// <summary>Crea el destinatario de un correo. El email se guarda recortado y en minúsculas.</summary>
    public static clsResultado<clsCorreoDestinatario> Crear(Guid correoId, string email, TipoDestinatario tipo)
    {
        if (!Enum.IsDefined(tipo))
        {
            return new clsErrorDominio(clsCorreo.CodigoError, "El tipo de destinatario debe ser 'para' o 'cc'.");
        }

        var error = clsValidacion.Requerido(clsCorreo.CodigoError, "email", email, LargoMaximoEmail);
        if (error is not null)
        {
            return error;
        }

        // Un carácter de control (CR, LF, NUL...) se rechaza ANTES de recortar: no se sanea en silencio, y el mensaje no
        // lo repite (llegaría tal cual al log y a la respuesta).
        if (clsCabecerasCorreo.TieneCaracteresDeControl(email))
        {
            return new clsErrorDominio(clsCorreo.CodigoError, MensajeConCaracteresDeControl);
        }

        var normalizado = Normalizar(email);
        // Una sola dirección por destinatario. MailSender separa una entrada con ',' o ';' en varias direcciones
        // (RecipientList), así que "a@x.com,b@y.com" llegaría a dos personas mientras Cotifly guarda una: se rechaza, igual que un nombre
        // con ángulos o comillas y una segunda arroba. Se valida con el formato estricto de clsCabecerasCorreo (parte local y dominio).
        if (!clsCabecerasCorreo.EsDireccionValida(normalizado))
        {
            return new clsErrorDominio(clsCorreo.CodigoError, $"'{normalizado}' no es un email válido.");
        }

        return new clsCorreoDestinatario
        {
            Id = Guid.NewGuid(),
            CorreoId = correoId,
            Email = normalizado,
            Tipo = tipo,
        };
    }

    /// <summary>Forma canónica de un email para comparar conjuntos de destinatarios.</summary>
    public static string Normalizar(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }
}
