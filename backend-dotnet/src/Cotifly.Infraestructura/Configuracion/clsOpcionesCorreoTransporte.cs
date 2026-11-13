using System.Net.Mail;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Dominio.Correo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Transporte del correo (sección <c>Correo</c>, la misma de <c>clsOpcionesCorreo</c> de Aplicación; ADR-023): el único
/// transporte es el microservicio interno MailSender (<c>POST /api/v1/emails</c>, multipart, 202 con un id), que encola el correo, lo
/// entrega por el relay corporativo y lo reintenta. Con el correo habilitado, los modos <c>smtp_relay</c>, <c>smtp</c> y
/// <c>wscorreos</c> impiden arrancar la API.
/// </summary>
public sealed class clsOpcionesCorreoTransporte
{
    public const string Seccion = "Correo";
    public const string ModoMailSender = "mailsender";

    /// <summary>Mismo valor que <c>clsOpcionesCorreo.Habilitado</c>: las reglas del transporte solo aplican si está habilitado.</summary>
    public bool Habilitado { get; set; }

    /// <summary><c>mailsender</c> (el único; por defecto).</summary>
    public string? Modo { get; set; } = ModoMailSender;

    /// <summary>MailSender (<c>Correo:MailSender</c>).</summary>
    public clsOpcionesMailSender MailSender { get; set; } = new();

    /// <summary><see cref="Modo"/> sin espacios y en minúsculas; vacío equivale a <c>mailsender</c>.</summary>
    public string ModoNormalizado => string.IsNullOrWhiteSpace(Modo) ? ModoMailSender : Modo.Trim().ToLowerInvariant();
}

/// <summary>
/// <c>Correo:MailSender</c>. La URL va SOLO en configuración (variable <c>Correo__MailSender__UrlBase</c> del stack); MailSender no
/// tiene autenticación (red interna), así que no hay secreto.
/// </summary>
public sealed class clsOpcionesMailSender
{
    /// <summary>Raíz de MailSender (sin <c>/api/v1</c>). Obligatoria con el correo habilitado.</summary>
    public string? UrlBase { get; set; }

    /// <summary>Tiempo máximo de cada llamada a MailSender (segundos, por defecto 15). El 202 llega en cuanto encola.</summary>
    public int TimeoutSegundos { get; set; } = 15;

    /// <summary>El <c>From</c> es el correo del comercial (por defecto; las respuestas del cliente le llegan a él).</summary>
    public const string RemitenteComercial = "comercial";

    /// <summary>El <c>From</c> es siempre <see cref="Remitente"/> (el comercial va en la firma del cuerpo y en copia oculta).</summary>
    public const string RemitenteCorporativo = "corporativo";

    /// <summary>
    /// Remitente corporativo: el <c>From</c> con <see cref="ModoRemitente"/>=<c>corporativo</c>, o cuando el correo del comercial falta, no es
    /// válido o su dominio no está en <see cref="DominiosRemitente"/>. MailSender acepta cualquier dirección válida en <c>From</c> (no tiene
    /// lista blanca); quien decide es el relay corporativo (SPF/DMARC del dominio).
    /// </summary>
    public string Remitente { get; set; } = "notificaciones@boterosoto.com.co";

    /// <summary>
    /// De quién es el <c>From</c>. <c>comercial</c> (por defecto): el correo del comercial, porque MailSender no tiene
    /// <c>Reply-To</c> y así las respuestas le llegan a él. <c>corporativo</c>: siempre <see cref="Remitente"/>, para cuando SPF/DMARC del
    /// dominio no autoricen al relay a enviar como cada comercial (un 550 5.7.1 es un fallo permanente).
    /// </summary>
    public string ModoRemitente { get; set; } = RemitenteComercial;

    /// <summary>
    /// Dominios con los que el comercial puede ir en el <c>From</c> (separados por coma; por defecto <c>boterosoto.com.co</c>): un correo de
    /// otro dominio (personal, de un aliado) fallaría DMARC, así que sale con <see cref="Remitente"/>.
    /// </summary>
    public string DominiosRemitente { get; set; } = "boterosoto.com.co";

    /// <summary><see cref="ModoRemitente"/> sin espacios y en minúsculas; vacío equivale a <c>comercial</c>.</summary>
    public string ModoRemitenteNormalizado =>
        string.IsNullOrWhiteSpace(ModoRemitente) ? RemitenteComercial : ModoRemitente.Trim().ToLowerInvariant();

    /// <summary>
    /// El <c>From</c> del mensaje: el correo del comercial si el modo es <c>comercial</c> y es UNA dirección válida de un dominio permitido;
    /// si no, <see cref="Remitente"/>. Nunca devuelve una dirección mal formada que MailSender rechazaría (422) o el relay no aceptaría.
    /// </summary>
    public string ElegirRemitente(string? correoComercial)
    {
        if (ModoRemitenteNormalizado != RemitenteComercial || string.IsNullOrWhiteSpace(correoComercial))
        {
            return Remitente;
        }

        // Además, el formato estricto de las cabeceras (clsCabecerasCorreo). Una dirección que MailAddress acepta pero el
        // transporte bloquearía (parte local entre comillas, dominio de una sola etiqueta) sale con el remitente corporativo, no falla el envío.
        var candidato = correoComercial.Trim();
        if (!clsCabecerasCorreo.EsDireccionValida(candidato) || !MailAddress.TryCreate(candidato, out var direccion)
            || !string.Equals(direccion.Address, candidato, StringComparison.OrdinalIgnoreCase))
        {
            return Remitente;
        }

        var dominios = DominiosRemitente.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return dominios.Length == 0 || dominios.Contains(direccion.Host, StringComparer.OrdinalIgnoreCase) ? candidato : Remitente;
    }

    /// <summary><c>Low</c>, <c>Normal</c> (por defecto) o <c>High</c>: cabeceras <c>Priority</c>/<c>Importance</c>/<c>X-Priority</c>.</summary>
    public string Prioridad { get; set; } = "Normal";

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSegundos);
}

/// <summary>Reglas de <see cref="clsOpcionesCorreoTransporte"/>; con el correo deshabilitado no se revisa nada (un modo heredado no impide arrancar).</summary>
public sealed class clsValidadorOpcionesCorreoTransporte : IValidateOptions<clsOpcionesCorreoTransporte>
{
    private const string Prefijo = clsOpcionesCorreoTransporte.Seccion + ":";
    private static readonly string[] _prioridades = ["Low", "Normal", "High"];

    public ValidateOptionsResult Validate(string? name, clsOpcionesCorreoTransporte options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Habilitado)
        {
            return ValidateOptionsResult.Success;
        }

        if (options.ModoNormalizado != clsOpcionesCorreoTransporte.ModoMailSender)
        {
            return ValidateOptionsResult.Fail(
                $"'{Prefijo}Modo={options.ModoNormalizado}' ya no existe (HU-033, ADR-023): el correo sale por MailSender. Use '{clsOpcionesCorreoTransporte.ModoMailSender}'.");
        }

        var errores = new List<string>();
        var mailSender = options.MailSender;
        if (!Uri.TryCreate(mailSender.UrlBase, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            errores.Add($"'{Prefijo}MailSender:UrlBase' es obligatorio con {Prefijo}Habilitado=true y debe ser una URL http(s) absoluta.");
        }

        // El mismo formato estricto que el transporte exige al From (si no, ningún correo saldría).
        if (!clsCabecerasCorreo.EsDireccionValida(mailSender.Remitente))
        {
            errores.Add($"'{Prefijo}MailSender:Remitente' debe ser un correo válido (una sola dirección, sin nombre ni espacios).");
        }

        if (mailSender.TimeoutSegundos is < 1 or > 120)
        {
            errores.Add($"'{Prefijo}MailSender:TimeoutSegundos' debe estar entre 1 y 120.");
        }

        if (!_prioridades.Contains(mailSender.Prioridad, StringComparer.Ordinal))
        {
            errores.Add($"'{Prefijo}MailSender:Prioridad' debe ser Low, Normal o High.");
        }

        if (mailSender.ModoRemitenteNormalizado is not (clsOpcionesMailSender.RemitenteComercial or clsOpcionesMailSender.RemitenteCorporativo))
        {
            errores.Add($"'{Prefijo}MailSender:ModoRemitente' debe ser '{clsOpcionesMailSender.RemitenteComercial}' o '{clsOpcionesMailSender.RemitenteCorporativo}'.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}

/// <summary>
/// Reglas de <see cref="clsOpcionesCorreo"/>. La protección es segura por defecto y NO depende del ambiente (QA y
/// staging corren como <c>Production</c>): con el correo habilitado, el envío real exige las dos llaves <c>Proteccion:Modo=ninguno</c> y
/// <c>Proteccion:PermitirEnvioReal=true</c> (una sin la otra impide arrancar) y, además, <c>Production</c>; en cualquier otro caso se redirige
/// y el <c>BuzonPruebas</c> válido es obligatorio. El modo siempre debe ser uno conocido.
/// </summary>
public sealed class clsValidadorOpcionesCorreo(IHostEnvironment entorno) : IValidateOptions<clsOpcionesCorreo>
{
    private const string Prefijo = clsOpcionesCorreo.Seccion + ":";

    public ValidateOptionsResult Validate(string? name, clsOpcionesCorreo options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();
        var proteccion = options.Proteccion ?? new clsOpcionesProteccionCorreo();
        var modo = proteccion.ModoNormalizado;
        if (modo is not (clsOpcionesProteccionCorreo.ModoRedirigir or clsOpcionesProteccionCorreo.ModoNinguno))
        {
            errores.Add($"'{Prefijo}Proteccion:Modo' debe ser '{clsOpcionesProteccionCorreo.ModoRedirigir}' o '{clsOpcionesProteccionCorreo.ModoNinguno}'.");
        }
        else if (options.Habilitado)
        {
            var ninguno = modo == clsOpcionesProteccionCorreo.ModoNinguno;
            if (ninguno != proteccion.PermitirEnvioReal)
            {
                // Las dos llaves van juntas: una sola es casi siempre un error de configuración (y nunca habilita el envío real).
                errores.Add($"El envío a destinatarios reales exige las dos llaves juntas: '{Prefijo}Proteccion:Modo={clsOpcionesProteccionCorreo.ModoNinguno}' "
                            + $"y '{Prefijo}Proteccion:PermitirEnvioReal=true'. Para redirigir al buzón de pruebas deje Modo={clsOpcionesProteccionCorreo.ModoRedirigir} "
                            + "y PermitirEnvioReal=false.");
            }

            if (!entorno.IsProduction() && (ninguno || proteccion.PermitirEnvioReal))
            {
                errores.Add($"Fuera de Production (ambiente '{entorno.EnvironmentName}') '{Prefijo}Proteccion:Modo' debe ser "
                            + $"'{clsOpcionesProteccionCorreo.ModoRedirigir}' y PermitirEnvioReal=false: ningún correo de pruebas puede llegar a un cliente.");
            }

            if (proteccion.Redirige && clsProteccionCorreo.BuzonValido(proteccion) is null)
            {
                errores.Add($"'{Prefijo}Proteccion:BuzonPruebas' es obligatorio (un solo correo válido) mientras el correo se redirige "
                            + $"('{Prefijo}Proteccion:Modo={clsOpcionesProteccionCorreo.ModoRedirigir}', el valor por defecto).");
            }
        }

        // El tope de destinatarios vale siempre (también con el correo deshabilitado, porque la vista previa lo aplica).
        if (options.MaximoDestinatarios is < 1 or > clsOpcionesCorreo.LimiteMaximoDestinatarios)
        {
            errores.Add($"'{Prefijo}MaximoDestinatarios' debe estar entre 1 y {clsOpcionesCorreo.LimiteMaximoDestinatarios} "
                        + $"(por defecto {clsOpcionesCorreo.MaximoDestinatariosPorDefecto}).");
        }

        var seguimiento = options.Seguimiento ?? new clsOpcionesSeguimientoCorreo();
        if (seguimiento.IntervaloTickSegundos is < 1 or > 3600 || seguimiento.IntervaloConsultaSegundos is < 1 or > 3600)
        {
            errores.Add($"'{Prefijo}Seguimiento:IntervaloTickSegundos' e 'IntervaloConsultaSegundos' deben estar entre 1 y 3600.");
        }

        if (seguimiento.EdadMaximaHoras is < 1 or > 720 || seguimiento.Lote is < 1 or > 1000 || seguimiento.ReclamoSegundos is < 1 or > 3600)
        {
            errores.Add($"'{Prefijo}Seguimiento': EdadMaximaHoras entre 1 y 720, Lote entre 1 y 1000 y ReclamoSegundos entre 1 y 3600.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
