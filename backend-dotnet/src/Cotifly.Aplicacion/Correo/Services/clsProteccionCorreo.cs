using Cotifly.Aplicacion.Correo.DTOs;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// HU-033 (#199, ADR-023, decisión 3): protección de los ambientes que no son producción (<c>Correo:Proteccion:Modo=redirigir</c>). El
/// mensaje que llega al transporte va SOLO al buzón de pruebas (sin CC ni CCO reales), con <c>[QA]</c> en el asunto y un aviso al inicio
/// del cuerpo con los destinatarios originales (para, CC y CCO). En la base se guardan los destinatarios ORIGINALES y el buzón en
/// <c>Correos.redirigidoA</c>, así la deduplicación, los destinatarios previos y el historial se comportan igual que en producción.
/// </summary>
public static class clsProteccionCorreo
{
    /// <summary>Aviso de la vista previa cuando el ambiente redirige los correos.</summary>
    public static string AvisoVistaPrevia(string buzon) =>
        $"Ambiente de pruebas: el correo NO llegará a los destinatarios; se redirige al buzón de pruebas {buzon} con el asunto marcado [QA].";

    /// <summary>
    /// El mensaje que sale al transporte y el buzón al que se redirigió (<c>null</c> si va a los destinatarios reales). Solo con
    /// <see cref="clsOpcionesProteccionCorreo.EnviaADestinatariosReales"/> (<c>Modo=ninguno</c> y <c>PermitirEnvioReal=true</c>) el mensaje no
    /// cambia. Falla CERRADO: si hay que redirigir y no hay un buzón válido, lanza <see cref="InvalidOperationException"/> (el correo no sale)
    /// en vez de dejarlo ir a los destinatarios reales.
    /// </summary>
    public static (clsMensajeCorreo Mensaje, string? RedirigidoA) Aplicar(clsMensajeCorreo mensaje, clsOpcionesProteccionCorreo opciones, Func<string, string> html)
    {
        ArgumentNullException.ThrowIfNull(mensaje);
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(html);
        if (opciones.EnviaADestinatariosReales)
        {
            return (mensaje, null);
        }

        var buzon = BuzonValido(opciones) ?? throw new InvalidOperationException(MensajeSinBuzon);
        var aviso = AvisoDestinatarios(mensaje);
        var asunto = mensaje.Asunto.StartsWith(clsOpcionesProteccionCorreo.PrefijoAsunto, StringComparison.Ordinal)
            ? mensaje.Asunto
            : clsOpcionesProteccionCorreo.PrefijoAsunto + mensaje.Asunto;
        var redirigido = mensaje with
        {
            Para = [buzon],
            Cc = [],
            Bcc = [],
            Asunto = asunto,
            CuerpoTexto = aviso + "\n\n" + mensaje.CuerpoTexto,
            CuerpoHtml = html(aviso),
        };
        return (redirigido, buzon);
    }

    /// <summary>Error cuando hay que redirigir y no hay buzón: el correo no sale (nunca va a los destinatarios reales por omisión).</summary>
    public const string MensajeSinBuzon =
        "La protección del correo redirige al buzón de pruebas pero 'Correo:Proteccion:BuzonPruebas' no es un correo válido: el correo no se envió.";

    /// <summary>El buzón de pruebas si es un correo válido (una sola dirección, sin nombre ni lista); <c>null</c> si no.</summary>
    public static string? BuzonValido(clsOpcionesProteccionCorreo opciones)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        var buzon = opciones.BuzonPruebas?.Trim();
        return !string.IsNullOrEmpty(buzon) && System.Net.Mail.MailAddress.TryCreate(buzon, out var direccion)
               && string.Equals(direccion.Address, buzon, StringComparison.OrdinalIgnoreCase)
            ? buzon
            : null;
    }

    /// <summary>"[QA] Ambiente de pruebas: … Destinatarios originales — Para: …; CC: …; CCO: …".</summary>
    public static string AvisoDestinatarios(clsMensajeCorreo mensaje)
    {
        ArgumentNullException.ThrowIfNull(mensaje);
        static string Lista(IReadOnlyList<string> correos) => correos.Count == 0 ? "(ninguno)" : string.Join(", ", correos);
        return "[QA] Ambiente de pruebas: este correo se redirigió al buzón de pruebas y no llegó a sus destinatarios. "
               + $"Destinatarios originales — Para: {Lista(mensaje.Para)}; CC: {Lista(mensaje.Cc)}; CCO: {Lista(mensaje.Bcc)}.";
    }
}
