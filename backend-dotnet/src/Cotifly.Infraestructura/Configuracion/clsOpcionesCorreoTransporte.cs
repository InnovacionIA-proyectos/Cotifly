using System.Net.Mail;
using Cotifly.Aplicacion.Correo.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Transporte del correo (sección <c>Correo</c>, la misma de <c>clsOpcionesCorreo</c> de Aplicación). HU-033 (#199, ADR-023): el único
/// transporte es el microservicio interno MailSender (<c>POST /api/v1/emails</c>, multipart, 202 con un id), que encola el correo, lo
/// entrega por el relay corporativo y lo reintenta. Los modos <c>smtp_relay</c> (MailKit directo al relay), <c>smtp</c> y
/// <c>wscorreos</c> se retiraron: con el correo habilitado la API no arranca con ellos.
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
/// HU-033: <c>Correo:MailSender</c>. La URL va SOLO en configuración (variable <c>Correo__MailSender__UrlBase</c> del stack); MailSender no
/// tiene autenticación (red interna), así que no hay secreto.
/// </summary>
public sealed class clsOpcionesMailSender
{
    /// <summary>Raíz de MailSender (sin <c>/api/v1</c>). Obligatoria con el correo habilitado.</summary>
    public string? UrlBase { get; set; }

    /// <summary>Tiempo máximo de cada llamada a MailSender (segundos, por defecto 15). El 202 llega en cuanto encola.</summary>
    public int TimeoutSegundos { get; set; } = 15;

    /// <summary>
    /// Remitente cuando el comercial no tiene correo en <c>UsuariosWeb</c>. MailSender acepta cualquier dirección válida en <c>From</c> (no
    /// tiene lista blanca); quien decide es el relay corporativo (SPF/DMARC del dominio).
    /// </summary>
    public string Remitente { get; set; } = "notificaciones@boterosoto.com.co";

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

        if (!MailAddress.TryCreate(mailSender.Remitente, out _))
        {
            errores.Add($"'{Prefijo}MailSender:Remitente' debe ser un correo válido.");
        }

        if (mailSender.TimeoutSegundos is < 1 or > 120)
        {
            errores.Add($"'{Prefijo}MailSender:TimeoutSegundos' debe estar entre 1 y 120.");
        }

        if (!_prioridades.Contains(mailSender.Prioridad, StringComparer.Ordinal))
        {
            errores.Add($"'{Prefijo}MailSender:Prioridad' debe ser Low, Normal o High.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}

/// <summary>
/// HU-033 (#199, decisión 3): reglas de <see cref="clsOpcionesCorreo"/>. Con el correo habilitado y fuera de <c>Production</c>, la
/// protección <c>redirigir</c> con un <c>BuzonPruebas</c> válido es OBLIGATORIA: la API no arranca sin ella. En <c>Production</c> se
/// admite <c>ninguno</c> (y <c>redirigir</c>, para un piloto controlado). El modo siempre debe ser uno conocido.
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
            if (!entorno.IsProduction() && modo != clsOpcionesProteccionCorreo.ModoRedirigir)
            {
                errores.Add($"Fuera de Production (ambiente '{entorno.EnvironmentName}') '{Prefijo}Proteccion:Modo' debe ser "
                            + $"'{clsOpcionesProteccionCorreo.ModoRedirigir}': ningún correo de pruebas puede llegar a un cliente.");
            }

            if (modo == clsOpcionesProteccionCorreo.ModoRedirigir && !MailAddress.TryCreate(proteccion.BuzonPruebas?.Trim(), out _))
            {
                errores.Add($"'{Prefijo}Proteccion:BuzonPruebas' es obligatorio (un correo válido) con '{Prefijo}Proteccion:Modo={clsOpcionesProteccionCorreo.ModoRedirigir}'.");
            }
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
