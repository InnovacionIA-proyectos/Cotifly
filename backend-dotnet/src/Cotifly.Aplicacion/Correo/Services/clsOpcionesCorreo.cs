namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Opciones del envío de correo que consume el caso de uso (sección <c>Correo</c>; el transporte lee la misma sección en
/// <c>clsOpcionesCorreoTransporte</c> de Infraestructura). Port de <c>correo_habilitado</c> de <c>shared/config.py</c>; HU-033 (#199,
/// ADR-023) agrega la protección fuera de producción y la consulta del estado en MailSender.
/// </summary>
public sealed class clsOpcionesCorreo
{
    public const string Seccion = "Correo";

    /// <summary>
    /// Si es <c>false</c> (por defecto, igual que el Python: "funcionalidad diferida") ningún correo sale y el envío responde
    /// <c>CORREO_DESHABILITADO</c>; la vista previa, los destinatarios previos y el historial siguen funcionando.
    /// </summary>
    public bool Habilitado { get; set; }

    /// <summary>Protección de los ambientes que no son producción (<c>Correo:Proteccion</c>).</summary>
    public clsOpcionesProteccionCorreo Proteccion { get; set; } = new();

    /// <summary>Consulta del estado de los correos en MailSender (<c>Correo:Seguimiento</c>).</summary>
    public clsOpcionesSeguimientoCorreo Seguimiento { get; set; } = new();
}

/// <summary>
/// HU-033 (#199): <c>Correo:Proteccion</c>. Fuera de <c>Production</c> el modo <see cref="ModoRedirigir"/> es OBLIGATORIO (con el correo
/// habilitado, la API no arranca sin él ni sin <see cref="BuzonPruebas"/>): ningún correo de QA o desarrollo llega a un cliente.
/// </summary>
public sealed class clsOpcionesProteccionCorreo
{
    /// <summary>Todo correo va al buzón de pruebas (sin CC ni CCO reales), con <c>[QA]</c> en el asunto y los destinatarios originales en el cuerpo.</summary>
    public const string ModoRedirigir = "redirigir";

    /// <summary>Sin protección: el correo va a los destinatarios reales (solo en producción).</summary>
    public const string ModoNinguno = "ninguno";

    /// <summary>Prefijo del asunto de un correo redirigido.</summary>
    public const string PrefijoAsunto = "[QA] ";

    /// <summary><see cref="ModoRedirigir"/> (por defecto) o <see cref="ModoNinguno"/>.</summary>
    public string? Modo { get; set; } = ModoRedirigir;

    /// <summary>Buzón que recibe todos los correos con <see cref="ModoRedirigir"/>. Obligatorio en ese modo (sin valor por defecto).</summary>
    public string? BuzonPruebas { get; set; }

    /// <summary><see cref="Modo"/> sin espacios y en minúsculas; vacío equivale a <see cref="ModoRedirigir"/>.</summary>
    public string ModoNormalizado => string.IsNullOrWhiteSpace(Modo) ? ModoRedirigir : Modo.Trim().ToLowerInvariant();

    /// <summary>¿Los correos se redirigen al buzón de pruebas?</summary>
    public bool Redirige => ModoNormalizado == ModoRedirigir;
}

/// <summary>
/// HU-033 (#199): <c>Correo:Seguimiento</c>, la consulta del estado en MailSender que hace el worker de correos
/// (<c>clsWorkerEstadoCorreos</c>). Los reintentos de entrega los hace MailSender (5 intentos con espera exponencial de 30 s a 15 min).
/// </summary>
public sealed class clsOpcionesSeguimientoCorreo
{
    /// <summary>Cada cuánto despierta el worker (segundos, por defecto 15).</summary>
    public int IntervaloTickSegundos { get; set; } = 15;

    /// <summary>Espera entre dos consultas del mismo correo (segundos, por defecto 30).</summary>
    public int IntervaloConsultaSegundos { get; set; } = 30;

    /// <summary>
    /// Pasadas estas horas desde el envío sin que MailSender confirme el resultado, el correo queda <c>fallido</c> (se puede reintentar).
    /// Por defecto 24: los reintentos de MailSender terminan en menos de 30 minutos.
    /// </summary>
    public int EdadMaximaHoras { get; set; } = 24;

    /// <summary>Correos que se consultan por vuelta (por defecto 50).</summary>
    public int Lote { get; set; } = 50;

    /// <summary>Tiempo que una réplica reserva un correo mientras lo consulta (segundos, por defecto 60).</summary>
    public int ReclamoSegundos { get; set; } = 60;

    public TimeSpan Intervalo => TimeSpan.FromSeconds(IntervaloConsultaSegundos);

    public TimeSpan EdadMaxima => TimeSpan.FromHours(EdadMaximaHoras);

    public TimeSpan Reclamo => TimeSpan.FromSeconds(ReclamoSegundos);
}
