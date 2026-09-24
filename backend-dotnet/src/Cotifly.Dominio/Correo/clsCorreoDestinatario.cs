using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Correo;

/// <summary>Destinatario de un correo (<c>dbo_cotifly.CorreoDestinatarios</c>), tipo <c>para</c> o <c>cc</c>.</summary>
public sealed class clsCorreoDestinatario
{
    public const int LargoMaximoEmail = 320;

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

        var normalizado = Normalizar(email);
        var arroba = normalizado.IndexOf('@', StringComparison.Ordinal);
        if (arroba <= 0 || arroba == normalizado.Length - 1 || normalizado.Contains(' ', StringComparison.Ordinal))
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
