using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Correo;

/// <summary>
/// Correo enviado (o intentado) de una cotización (<c>dbo_cotifly.Correos</c>) con sus destinatarios
/// (<c>CorreoDestinatarios</c>). Invariante de aplicación (§5.4 del diseño, no expresable como CHECK):
/// al menos un destinatario <c>para</c>.
/// </summary>
public sealed class clsCorreo
{
    public const string CodigoError = "CORREO_INVALIDO";

    private readonly List<clsCorreoDestinatario> _destinatarios = [];

    private clsCorreo()
    {
    }

    public Guid Id { get; private set; }
    public Guid CotizacionId { get; private set; }
    public string? Asunto { get; private set; }
    public string? MessageId { get; private set; }
    public Guid? ConfirmadoPor { get; private set; }
    public EstadoCorreo? Estado { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyCollection<clsCorreoDestinatario> Destinatarios => _destinatarios.AsReadOnly();

    /// <summary>Emails <c>para</c>, normalizados.</summary>
    public IReadOnlyList<string> Para => [.. _destinatarios.Where(d => d.Tipo == TipoDestinatario.Para).Select(d => d.Email)];

    /// <summary>Emails <c>cc</c>, normalizados.</summary>
    public IReadOnlyList<string> Cc => [.. _destinatarios.Where(d => d.Tipo == TipoDestinatario.Cc).Select(d => d.Email)];

    /// <summary>
    /// Registra un correo. Exige al menos un <c>para</c>; los emails repetidos (sin distinguir
    /// mayúsculas) dentro del mismo tipo se guardan una sola vez.
    /// </summary>
    public static clsResultado<clsCorreo> Crear(
        Guid cotizacionId,
        IEnumerable<string> para,
        EstadoCorreo estado,
        string? asunto = null,
        IEnumerable<string>? cc = null,
        Guid? confirmadoPor = null,
        string? messageId = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(para);

        if (cotizacionId == Guid.Empty)
        {
            return new clsErrorDominio(CodigoError, "El correo debe pertenecer a una cotización.");
        }

        var correo = new clsCorreo
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            Asunto = clsValidacion.Normalizar(asunto),
            MessageId = clsValidacion.Normalizar(messageId),
            ConfirmadoPor = confirmadoPor,
            Estado = estado,
            Error = clsValidacion.Normalizar(error),
        };

        var falla = correo.AgregarDestinatarios(para, TipoDestinatario.Para)
            ?? correo.AgregarDestinatarios(cc ?? [], TipoDestinatario.Cc);
        if (falla is not null)
        {
            return falla;
        }

        if (correo.Para.Count == 0)
        {
            return new clsErrorDominio(CodigoError, "El correo debe tener al menos un destinatario 'para'.");
        }

        return correo;
    }

    /// <summary>¿Los destinatarios <c>para</c> son el mismo conjunto (sin distinguir mayúsculas ni orden)?</summary>
    public bool TieneMismosPara(IEnumerable<string> para)
    {
        ArgumentNullException.ThrowIfNull(para);
        var objetivo = para.Where(p => !string.IsNullOrWhiteSpace(p)).Select(clsCorreoDestinatario.Normalizar).ToHashSet(StringComparer.Ordinal);
        return objetivo.SetEquals(Para);
    }

    private clsErrorDominio? AgregarDestinatarios(IEnumerable<string> emails, TipoDestinatario tipo)
    {
        foreach (var email in emails)
        {
            var destinatario = clsCorreoDestinatario.Crear(Id, email, tipo);
            if (destinatario.EsFallo)
            {
                return destinatario.Error;
            }

            if (!_destinatarios.Any(d => d.Tipo == tipo && d.Email == destinatario.Valor.Email))
            {
                _destinatarios.Add(destinatario.Valor);
            }
        }

        return null;
    }
}
