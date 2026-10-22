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

    // HU-033 (#199, ADR-023, script 13): seguimiento del envío asíncrono por MailSender.

    /// <summary>Id del correo en MailSender (<c>id</c> del 202); <c>null</c> si MailSender no lo aceptó o el correo es anterior al 13.</summary>
    public Guid? IdMailSender { get; private set; }

    /// <summary>
    /// Buzón de pruebas al que se redirigió el correo fuera de producción (<c>Correo:Proteccion:Modo=redirigir</c>); <c>null</c> si salió a los
    /// destinatarios reales. Los destinatarios guardados son siempre los ORIGINALES (deduplicación y destinatarios previos iguales que en producción).
    /// </summary>
    public string? RedirigidoA { get; private set; }

    /// <summary>Intentos de entrega al relay que lleva MailSender (<c>attemptCount</c>); <c>null</c> si todavía no se consultó.</summary>
    public int? IntentosEntrega { get; private set; }

    /// <summary>Cuándo el relay aceptó el correo (<c>sentAt</c> de MailSender).</summary>
    public DateTimeOffset? EnviadoAt { get; private set; }

    /// <summary>Última vez que Cotifly consultó el estado en MailSender.</summary>
    public DateTimeOffset? EstadoActualizadoAt { get; private set; }

    /// <summary>Agenda de la consulta del estado (worker de correos): <c>null</c> = ya no se consulta (estado final).</summary>
    public DateTimeOffset? SiguienteConsultaAt { get; private set; }

    /// <summary>¿MailSender todavía no confirmó el resultado? (<see cref="EstadoCorreo.Encolado"/> o <see cref="EstadoCorreo.Enviando"/>.)</summary>
    public bool EnCurso => Estado is EstadoCorreo.Encolado or EstadoCorreo.Enviando;

    /// <summary>
    /// ¿Cuenta para la deduplicación de 5 minutos? Los que salieron o siguen en curso en MailSender (saldrán, salvo fallo); los fallidos no,
    /// para que el comercial pueda volver a enviar.
    /// </summary>
    public bool CuentaComoEnvio => Estado is EstadoCorreo.Enviado || EnCurso;

    /// <summary>¿Se puede pedir a MailSender que lo reintente? Solo un fallido que MailSender conoce (tiene <see cref="IdMailSender"/>).</summary>
    public bool EsReintentable => Estado == EstadoCorreo.Fallido && IdMailSender is not null;

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
        string? error = null,
        Guid? id = null,
        Guid? idMailSender = null,
        string? redirigidoA = null,
        DateTimeOffset? siguienteConsultaAt = null)
    {
        ArgumentNullException.ThrowIfNull(para);

        if (cotizacionId == Guid.Empty)
        {
            return new clsErrorDominio(CodigoError, "El correo debe pertenecer a una cotización.");
        }

        var correo = new clsCorreo
        {
            Id = id is { } propio && propio != Guid.Empty ? propio : Guid.NewGuid(),
            CotizacionId = cotizacionId,
            Asunto = clsValidacion.Normalizar(asunto),
            MessageId = clsValidacion.Normalizar(messageId),
            ConfirmadoPor = confirmadoPor,
            Estado = estado,
            Error = clsValidacion.Normalizar(error),
            IdMailSender = idMailSender,
            RedirigidoA = clsValidacion.Normalizar(redirigidoA),
            SiguienteConsultaAt = estado is EstadoCorreo.Encolado or EstadoCorreo.Enviando ? siguienteConsultaAt : null,
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

    /// <summary>Largo máximo del error guardado (el texto de MailSender puede ser largo).</summary>
    public const int LargoMaximoError = 2000;

    /// <summary>
    /// HU-033: aplica el estado que reporta MailSender. <paramref name="siguiente"/> es la próxima consulta si sigue en curso (se ignora en
    /// un estado final). Devuelve <c>true</c> si cambió algo que ve el comercial (estado, error, intentos o fecha de envío).
    /// </summary>
    public bool ActualizarEntrega(EstadoCorreo estado, string? error, int? intentos, DateTimeOffset? enviadoAt, DateTimeOffset ahora, DateTimeOffset? siguiente)
    {
        var errorLimpio = Recortar(error);
        var cambio = Estado != estado || Error != errorLimpio || IntentosEntrega != intentos || EnviadoAt != enviadoAt;
        Estado = estado;
        Error = errorLimpio;
        IntentosEntrega = intentos;
        EnviadoAt = enviadoAt;
        EstadoActualizadoAt = ahora;
        SiguienteConsultaAt = EnCurso ? siguiente : null;
        return cambio;
    }

    /// <summary>La consulta a MailSender falló (caído o sin respuesta): solo se reprograma, sin tocar el estado.</summary>
    public void ReprogramarConsulta(DateTimeOffset? siguiente) => SiguienteConsultaAt = EnCurso ? siguiente : null;

    /// <summary>
    /// MailSender no confirmó la entrega en la edad máxima de la consulta: queda <see cref="EstadoCorreo.Fallido"/> con el motivo (se puede
    /// reintentar: MailSender ignora el reintento de un correo que ya salió).
    /// </summary>
    public void VencerConsulta(string motivo, DateTimeOffset ahora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        Estado = EstadoCorreo.Fallido;
        Error = Recortar(motivo);
        EstadoActualizadoAt = ahora;
        SiguienteConsultaAt = null;
    }

    /// <summary>MailSender aceptó el reintento (202): vuelve a <see cref="EstadoCorreo.Encolado"/>, sin error, y se agenda la consulta.</summary>
    public void RegistrarReintento(DateTimeOffset ahora, DateTimeOffset siguiente)
    {
        if (!EsReintentable)
        {
            throw new InvalidOperationException("Solo se reintenta un correo fallido que MailSender conoce.");
        }

        Estado = EstadoCorreo.Encolado;
        Error = null;
        EstadoActualizadoAt = ahora;
        SiguienteConsultaAt = siguiente;
    }

    private static string? Recortar(string? texto)
    {
        var limpio = clsValidacion.Normalizar(texto);
        return limpio is { Length: > LargoMaximoError } ? limpio[..(LargoMaximoError - 1)] + "…" : limpio;
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
