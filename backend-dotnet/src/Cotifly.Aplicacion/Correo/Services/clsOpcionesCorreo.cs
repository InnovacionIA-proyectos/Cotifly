namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Opciones del envío de correo que consume el caso de uso (sección <c>Correo</c>; el transporte lee la misma sección en
/// <c>clsOpcionesCorreoTransporte</c> de Infraestructura). Port de <c>correo_habilitado</c> de <c>shared/config.py</c>; HU-033 (#199,
/// ADR-023) agrega la protección fuera de producción y la consulta del estado en MailSender.
/// </summary>
public sealed class clsOpcionesCorreo
{
    public const string Seccion = "Correo";

    /// <summary>#228 (decisión del 2026-10-27): tope de destinatarios por defecto entre <c>para</c> y <c>cc</c>.</summary>
    public const int MaximoDestinatariosPorDefecto = 10;

    /// <summary>#228: valor más alto que admite <see cref="MaximoDestinatarios"/>; uno mayor (o menor que 1) impide arrancar.</summary>
    public const int LimiteMaximoDestinatarios = 100;

    /// <summary>
    /// Si es <c>false</c> (por defecto, igual que el Python: "funcionalidad diferida") ningún correo sale y el envío responde
    /// <c>CORREO_DESHABILITADO</c>; la vista previa, los destinatarios previos y el historial siguen funcionando.
    /// </summary>
    public bool Habilitado { get; set; }

    /// <summary>
    /// #228 (decisión del 2026-10-27): tope de destinatarios entre <c>para</c> y <c>cc</c> juntos, sin contar la copia oculta al comercial
    /// (<c>Correo:MaximoDestinatarios</c>, variable <c>Correo__MaximoDestinatarios</c>). Por defecto <see cref="MaximoDestinatariosPorDefecto"/>;
    /// de 1 a <see cref="LimiteMaximoDestinatarios"/>, o la API no arranca. Pasado el tope, el envío y la vista previa responden
    /// <c>CORREO_DEMASIADOS_DESTINATARIOS</c> con <c>detalle.maximo</c> = este valor. MailSender no tiene tope propio.
    /// </summary>
    public int MaximoDestinatarios { get; set; } = MaximoDestinatariosPorDefecto;

    /// <summary>Protección de los ambientes que no son producción (<c>Correo:Proteccion</c>).</summary>
    public clsOpcionesProteccionCorreo Proteccion { get; set; } = new();

    /// <summary>Consulta del estado de los correos en MailSender (<c>Correo:Seguimiento</c>).</summary>
    public clsOpcionesSeguimientoCorreo Seguimiento { get; set; } = new();
}

/// <summary>
/// HU-033 (#199): <c>Correo:Proteccion</c>. SEGURA POR DEFECTO, sin depender de <c>ASPNETCORE_ENVIRONMENT</c> (QA y staging corren como
/// <c>Production</c>): todo correo se redirige al buzón de pruebas salvo que se configuren EXPLÍCITAMENTE las dos llaves
/// <c>Correo:Proteccion:Modo=ninguno</c> y <c>Correo:Proteccion:PermitirEnvioReal=true</c> (solo en producción). Un modo desconocido,
/// <c>ninguno</c> sin la confirmación o la confirmación sin <c>ninguno</c> siguen redirigiendo; con el correo habilitado la API no arranca con
/// una configuración incoherente ni sin un <see cref="BuzonPruebas"/> válido cuando redirige.
/// </summary>
public sealed class clsOpcionesProteccionCorreo
{
    /// <summary>Todo correo va al buzón de pruebas (sin CC ni CCO reales), con <c>[QA]</c> en el asunto y los destinatarios originales en el cuerpo.</summary>
    public const string ModoRedirigir = "redirigir";

    /// <summary>Sin protección: el correo va a los destinatarios reales (solo en producción y con <see cref="PermitirEnvioReal"/>).</summary>
    public const string ModoNinguno = "ninguno";

    /// <summary>Prefijo del asunto de un correo redirigido.</summary>
    public const string PrefijoAsunto = "[QA] ";

    /// <summary><see cref="ModoRedirigir"/> (por defecto) o <see cref="ModoNinguno"/>.</summary>
    public string? Modo { get; set; } = ModoRedirigir;

    /// <summary>Buzón que recibe todos los correos cuando se redirige. Obligatorio en ese caso (sin valor por defecto).</summary>
    public string? BuzonPruebas { get; set; }

    /// <summary>
    /// Segunda llave del envío real (por defecto <c>false</c>): solo con <c>Modo=ninguno</c> Y <c>PermitirEnvioReal=true</c> el correo va a los
    /// destinatarios reales. Así un <c>Correo__Proteccion__Modo=ninguno</c> copiado por error en QA no basta para escribirle a un cliente.
    /// </summary>
    public bool PermitirEnvioReal { get; set; }

    /// <summary><see cref="Modo"/> sin espacios y en minúsculas; vacío equivale a <see cref="ModoRedirigir"/>.</summary>
    public string ModoNormalizado => string.IsNullOrWhiteSpace(Modo) ? ModoRedirigir : Modo.Trim().ToLowerInvariant();

    /// <summary>¿El correo va a los destinatarios reales? Solo con las dos llaves: <c>Modo=ninguno</c> y <c>PermitirEnvioReal=true</c>.</summary>
    public bool EnviaADestinatariosReales => ModoNormalizado == ModoNinguno && PermitirEnvioReal;

    /// <summary>¿Los correos se redirigen al buzón de pruebas? Siempre, salvo <see cref="EnviaADestinatariosReales"/> (falla cerrado).</summary>
    public bool Redirige => !EnviaADestinatariosReales;
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
