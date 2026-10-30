using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Implementación de <see cref="IClsCorreoService"/> (port de <c>ServicioCorreo</c> de <c>mcp_server/correo.py</c> y de
/// <c>enviar_cotizacion</c> de <c>orquestador/web.py</c>; HU-033, #199, ADR-023: el transporte es MailSender). Invariantes:
/// <list type="bullet">
/// <item>Ningún correo sale sin el comercial autenticado (<c>confirmado_por</c>), que además debe ser el dueño de la cotización. El dueño se
/// comprueba antes que nada más (correo deshabilitado, destinatarios): un folio ajeno o inexistente siempre es <c>COTIZACION_NO_ENCONTRADA</c>.</item>
/// <item>Deduplicación: un envío a los mismos destinatarios <c>para</c> (sin distinguir mayúsculas ni orden) en los últimos 5 minutos que
/// salió o sigue en curso en MailSender (<c>enviado</c>, <c>encolado</c>, <c>enviando</c>) se rechaza; los fallidos no cuentan. La regla
/// vive aquí, no en el repositorio (deuda de ADR-008), y se mide con <see cref="IClsReloj"/>. Los envíos de una misma cotización se
/// serializan (<see cref="clsCandadoEnvios"/>) y, dentro del candado, la cotización se recarga de la base.</item>
/// <item>El PDF sale del almacén; sin PDF → <c>CORREO_PDF_NO_ENCONTRADO</c>.</item>
/// <item>Remitente: el correo del comercial en <c>UsuariosWeb</c> (MailSender no admite <c>Reply-To</c>: así las respuestas le llegan a
/// él). Copia oculta: el mismo comercial (decisión 4), salvo que ya esté en <c>para</c> o <c>cc</c>.</item>
/// <item>Cuerpo HTML con el bloque fijo del resumen: precio por ruta y total, sin desglose (decisión 5).</item>
/// <item>Fuera de producción (<c>Correo:Proteccion:Modo=redirigir</c>) el mensaje va solo al buzón de pruebas, con <c>[QA]</c> en el asunto
/// y los destinatarios originales en el cuerpo (<see cref="clsProteccionCorreo"/>); en la base quedan los originales y el buzón.</item>
/// <item>MailSender acepta (202): fila <c>encolado</c> con su id y la consulta agendada (el worker de correos la lleva a <c>enviado</c> o
/// <c>fallido</c>); <c>estadoEnvio</c> <c>sin_enviar</c> → <c>enviada</c> y <c>enviada</c>/<c>reenviada</c> → <c>reenviada</c>. Un fallo
/// posterior en MailSender NO devuelve el estado de envío: el comercial reintenta el mismo correo (<c>POST .../correos/{id}/reintentar</c>).</item>
/// <item>MailSender no acepta (400/413/422, caído o sin respuesta): fila <c>fallido</c> con el error, en una transacción propia, y
/// <c>CORREO_ENVIO_FALLIDO</c>; la reserva del estado de envío se deshace.</item>
/// <item>Concurrencia (ADR-018 §12): antes de enviar, el estado de envío se RESERVA (enviada/reenviada, versión de negocio + 1)
/// con <c>GuardarCambiosSiVersionAsync</c> sobre la versión leída. Así un ajuste de margen o de conexos concurrente pierde
/// (<c>CONFLICTO_CONCURRENCIA</c> o <c>PDF_YA_ENVIADO</c>) y nunca invalida un PDF que ya está saliendo; si el ajuste ganó antes, el
/// envío responde <c>CONFLICTO_CONCURRENCIA</c> sin enviar nada.</item>
/// </list>
/// </summary>
public sealed class clsCorreoService(
    IClsCotizacionRepository cotizaciones,
    IClsCorreoRepository correos,
    IClsUsuarioWebRepository usuarios,
    IClsAlmacenPdf almacen,
    IClsTransporteCorreo transporte,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    clsCandadoEnvios candado,
    IOptions<clsOpcionesCorreo> opciones,
    ILogger<clsCorreoService> logger) : IClsCorreoService
{
    /// <summary>Ventana de deduplicación del contrato: 5 minutos.</summary>
    public static readonly TimeSpan VentanaDuplicado = TimeSpan.FromMinutes(5);

    public const int MaxAsunto = 300;
    public const int MaxCuerpo = 5000;

    /// <summary>DEF-1027B-2 (#228): mensaje de <c>CORREO_INVALIDO</c> para un asunto con saltos de línea u otros caracteres de control.</summary>
    public const string MensajeAsuntoConControl =
        "El asunto no puede tener saltos de línea ni otros caracteres de control (va como cabecera del correo): escríbelo en una sola línea.";

    /// <summary>HU-036.3 (#207): cierre del mensaje de <c>COTIZACION_NO_FINALIZADA</c> al enviar la cotización.</summary>
    public const string AccionEnviar = "enviarla al cliente";

    /// <summary>Largo máximo del <c>X-Correlation-ID</c> que guarda MailSender.</summary>
    public const int LargoMaximoCorrelacion = 128;

    public async Task<clsResultado<clsSalidaEnvioCorreo>> EnviarAsync(clsEntradaEnviarCorreo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (entrada.UsuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ConfirmacionRequerida, "Falta 'confirmado_por': el envío no está autorizado.");
        }

        // Endurecimiento A (#199): primero el dueño. Un folio ajeno o inexistente responde 404 antes que CORREO_DESHABILITADO (503) o
        // CORREO_INVALIDO (400), que si no dejarían enumerar los folios de otros comerciales (como en reintentar).
        var cotizacion = await BuscarCotizacionAsync(entrada, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (!opciones.Value.Habilitado)
        {
            return new clsErrorDominio(clsCodigosError.CorreoDeshabilitado, "El envío de correo desde Cotifly está deshabilitado (Correo:Habilitado=false).");
        }

        // DEF-1027B-2 (#228): el borrador se valida TAL COMO LLEGA (antes de recortar), así un CR/LF al final tampoco se sanea en silencio.
        if (ValidarBorrador(entrada.Para, entrada.Cc, entrada.Asunto, entrada.Cuerpo, exigePara: true, opciones.Value.MaximoDestinatarios) is { } invalida)
        {
            return invalida;
        }

        var para = Limpiar(entrada.Para);
        var cc = Limpiar(entrada.Cc);
        var asunto = string.IsNullOrWhiteSpace(entrada.Asunto) ? null : entrada.Asunto.Trim();
        var cuerpo = string.IsNullOrWhiteSpace(entrada.Cuerpo) ? null : entrada.Cuerpo.Trim();

        var usuario = await usuarios.ObtenerPorIdAsync(entrada.UsuarioId, cancellationToken);

        using var turno = await candado.TomarAsync(cotizacion.Id, cancellationToken);

        // La cotización se leyó antes del candado: otro envío pudo cambiar estadoEnvio mientras tanto.
        await cotizaciones.RecargarAsync(cotizacion, cancellationToken);

        // HU-036.3 (#207): la cotización sale al cliente con el precio que aceptó el comercial.
        if (cotizacion.ValidarFinalizadaAntesDe(AccionEnviar) is { } sinFinalizar)
        {
            return sinFinalizar;
        }

        var recientes = await correos.ListarEnviadosDesdeAsync(cotizacion.Id, reloj.AhoraUtc - VentanaDuplicado, cancellationToken);
        if (recientes.Any(c => c.CuentaComoEnvio && c.TieneMismosPara(para)))
        {
            return new clsErrorDominio(clsCodigosError.CorreoDuplicado, "Envío duplicado a los mismos destinatarios en menos de 5 minutos.");
        }

        if (cotizacion.PdfDesactualizado)
        {
            // ADR-018 §11: un ajuste posterior invalidó el PDF (también si ganó una carrera contra este envío): no se envía.
            return new clsErrorDominio(clsCodigosError.ConflictoConcurrencia,
                $"El PDF de la cotización {cotizacion.Folio} quedó desactualizado por un cambio reciente: regenéralo antes de enviarlo.");
        }

        var pdf = await almacen.LeerPorReferenciaAsync(cotizacion.PdfPath, cancellationToken);
        if (pdf is null)
        {
            return new clsErrorDominio(clsCodigosError.CorreoPdfNoEncontrado, $"La cotización {cotizacion.Folio} no tiene PDF disponible.");
        }

        asunto ??= clsPlantillaCorreo.AsuntoPorDefecto(cotizacion.Folio);
        var texto = cuerpo ?? clsPlantillaCorreo.CuerpoPorDefecto;
        var correoId = Guid.NewGuid();
        var resumen = clsPlantillaCorreo.Resumen(cotizacion);
        var firma = clsPlantillaCorreo.FirmaComercial(usuario?.NombreCompleto, usuario?.Email, usuario?.Telefono);
        var original = new clsMensajeCorreo(
            usuario?.Email,
            para,
            cc,
            asunto,
            texto,
            clsPlantillaCorreo.Html(texto, resumen, firmaComercial: firma),
            new clsAdjuntoCorreo(NombreAdjunto(cotizacion), pdf))
        {
            Bcc = CopiaOculta(usuario?.Email, para, cc) is { } bcc ? [bcc] : [],
            IdCorrelacion = IdCorrelacion(cotizacion.Folio, correoId),
            Metadatos = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["origen"] = "cotifly",
                ["folio"] = cotizacion.Folio,
                ["correo_id"] = correoId.ToString("D"),
                ["cotizacion_id"] = cotizacion.Id.ToString("D"),
            },
        };
        clsMensajeCorreo mensaje;
        string? redirigidoA;
        try
        {
            (mensaje, redirigidoA) = clsProteccionCorreo.Aplicar(
                original, opciones.Value.Proteccion, aviso => clsPlantillaCorreo.Html(texto, resumen, aviso, firma));
        }
        catch (InvalidOperationException ex)
        {
            // Falla cerrado (HU-033): hay que redirigir y no hay buzón válido. Nada sale y la cotización no cambia.
            logger.LogError("Correo de la cotización {Folio} bloqueado por la protección: {Motivo}", cotizacion.Folio, ex.Message);
            return new clsErrorDominio(clsCodigosError.CorreoEnvioFallido, ex.Message);
        }

        // Reserva del envío (ADR-018 §12): sube la versión y deja la cotización enviada antes de que el correo salga.
        var estadoAnterior = cotizacion.EstadoEnvio;
        var versionLeida = cotizacion.VersionNegocio;
        cotizacion.RegistrarEnvio();
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionLeida, cancellationToken))
        {
            return new clsErrorDominio(clsCodigosError.ConflictoConcurrencia,
                $"La cotización {cotizacion.Folio} cambió mientras se preparaba el envío: recárgala y vuelve a intentarlo.");
        }

        clsEnvioAceptado aceptado;
        try
        {
            aceptado = await transporte.EnviarAsync(mensaje, cancellationToken);
        }
        catch (clsEnvioCorreoException ex)
        {
            logger.LogWarning("Falló el envío de la cotización {Folio} por {Modo} (incierto: {Incierto}): {Motivo}",
                cotizacion.Folio, transporte.Modo, ex.Incierto, ex.Message);
            if (ex.Incierto)
            {
                // Auditoría #199: MailSender no respondió al POST y pudo encolarlo. La reserva se CONSERVA (el PDF pudo salir: es la foto
                // entregada) y la fila incierta cuenta para la deduplicación de 5 minutos, así un reenvío inmediato no duplica el correo.
                await RegistrarFallidoAsync(cotizacion, correoId, para, cc, asunto, entrada.UsuarioId, clsCorreo.PrefijoIncierto + ex.Message, redirigidoA);
                return new clsErrorDominio(clsCodigosError.CorreoEnvioFallido, ex.Message);
            }

            await DeshacerReservaAsync(cotizacion, estadoAnterior);
            await RegistrarFallidoAsync(cotizacion, correoId, para, cc, asunto, entrada.UsuarioId, ex.Message, redirigidoA);
            return new clsErrorDominio(clsCodigosError.CorreoEnvioFallido, ex.Message);
        }

        var registrado = await RegistrarAceptadoAsync(cotizacion, correoId, para, cc, asunto, entrada.UsuarioId, aceptado, redirigidoA);
        var idExterno = aceptado.IdExterno?.ToString("D") ?? aceptado.MessageId ?? string.Empty;
        return new clsSalidaEnvioCorreo(Enviado: true, idExterno, registrado)
        {
            CorreoId = correoId,
            Estado = clsCodigoEnum.ACodigo(aceptado.Estado),
            RedirigidoA = redirigidoA,
        };
    }

    /// <summary><c>X-Correlation-ID</c> de MailSender: <c>{folio}:{id del correo en Cotifly}</c> (MailSender guarda hasta 128).</summary>
    public static string IdCorrelacion(string folio, Guid correoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var id = $"{folio.Trim()}:{correoId:D}";
        return id.Length <= LargoMaximoCorrelacion ? id : id[^LargoMaximoCorrelacion..];
    }

    /// <summary>Copia oculta al comercial (decisión 4): su correo, salvo que no tenga uno válido o ya esté en <c>para</c> o <c>cc</c>.</summary>
    public static string? CopiaOculta(string? correoComercial, IReadOnlyList<string> para, IReadOnlyList<string> cc)
    {
        ArgumentNullException.ThrowIfNull(para);
        ArgumentNullException.ThrowIfNull(cc);
        if (string.IsNullOrWhiteSpace(correoComercial) || clsCorreoDestinatario.Crear(Guid.NewGuid(), correoComercial, TipoDestinatario.Cc).EsFallo)
        {
            return null;
        }

        var normalizado = clsCorreoDestinatario.Normalizar(correoComercial);
        return para.Concat(cc).Any(d => clsCorreoDestinatario.Normalizar(d) == normalizado) ? null : correoComercial.Trim();
    }

    private Task<clsCotizacionSolicitud?> BuscarCotizacionAsync(clsEntradaEnviarCorreo entrada, CancellationToken cancellationToken)
    {
        if (entrada.CotizacionId is { } id && id != Guid.Empty)
        {
            return cotizaciones.ObtenerPorIdDelUsuarioAsync(id, entrada.UsuarioId, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(entrada.Folio)
            ? Task.FromResult<clsCotizacionSolicitud?>(null)
            : cotizaciones.ObtenerPorFolioDelUsuarioAsync(entrada.Folio, entrada.UsuarioId, cancellationToken);
    }

    private async Task<bool> RegistrarAceptadoAsync(
        clsCotizacionSolicitud cotizacion, Guid correoId, IReadOnlyList<string> para, IReadOnlyList<string> cc, string asunto, Guid usuarioId,
        clsEnvioAceptado aceptado, string? redirigidoA)
    {
        try
        {
            var correo = clsCorreo.Crear(cotizacion.Id, para, aceptado.Estado, asunto, cc, usuarioId, aceptado.MessageId,
                id: correoId, idMailSender: aceptado.IdExterno, redirigidoA: redirigidoA,
                siguienteConsultaAt: reloj.AhoraUtc + opciones.Value.Seguimiento.Intervalo, encoladoAt: reloj.AhoraUtc).Valor;
            correos.Agregar(correo);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None); // el estado de envío ya quedó en la reserva
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MailSender ya lo aceptó: se informa como enviado para que el comercial no lo reenvíe.
            logger.LogError(ex, "MailSender aceptó el correo de la cotización {Folio} ({IdExterno}) pero no se pudo registrar en la base.",
                cotizacion.Folio, aceptado.IdExterno);
            return false;
        }
    }

    /// <summary>
    /// El correo no salió: vuelve al estado de envío anterior (y sube la versión, porque el estado cambió). Si otra escritura cambió la
    /// versión entretanto (el polling), recarga y reintenta; ningún ajuste puede colarse porque la cotización figura enviada.
    /// </summary>
    private async Task DeshacerReservaAsync(clsCotizacionSolicitud cotizacion, EstadoEnvio? estadoAnterior)
    {
        for (var intento = 0; intento < 3; intento++)
        {
            try
            {
                if (intento > 0)
                {
                    await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
                }

                var version = cotizacion.VersionNegocio;
                cotizacion.RestaurarEstadoEnvio(estadoAnterior);
                cotizacion.IncrementarVersionNegocio();
                if (await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "No se pudo deshacer la reserva del envío de la cotización {Folio}.", cotizacion.Folio);
                return;
            }
        }

        logger.LogError("No se pudo deshacer la reserva del envío de la cotización {Folio}: queda marcada como enviada.", cotizacion.Folio);
    }

    private async Task RegistrarFallidoAsync(
        clsCotizacionSolicitud cotizacion, Guid correoId, IReadOnlyList<string> para, IReadOnlyList<string> cc, string asunto, Guid usuarioId,
        string error, string? redirigidoA)
    {
        try
        {
            var correo = clsCorreo.Crear(cotizacion.Id, para, EstadoCorreo.Fallido, asunto, cc, usuarioId, error: error,
                id: correoId, redirigidoA: redirigidoA).Valor;
            correos.Agregar(correo);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo registrar el envío fallido de la cotización {Folio}.", cotizacion.Folio);
        }
    }

    /// <summary>Nombre del adjunto: el del archivo guardado (<c>PCN-...pdf</c>) o, si no se puede leer de la referencia, el folio.</summary>
    public static string NombreAdjunto(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var nombre = string.IsNullOrWhiteSpace(cotizacion.PdfPath) ? null : Path.GetFileName(cotizacion.PdfPath.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(nombre) ? $"{cotizacion.Folio}.pdf" : nombre;
    }

    /// <summary>
    /// DEF-1027B-2 y tope (#228, ADR-023): el borrador del comercial (<c>EnvioWeb</c>) tal como llega, antes de recortar nada. Lo usan el
    /// envío y la vista previa, en este orden:
    /// <list type="number">
    /// <item>asunto con caracteres de control (CR, LF, NUL, tabulador, U+0085, U+2028, U+2029...): <c>CORREO_INVALIDO</c>
    /// (<see cref="MensajeAsuntoConControl"/>). El asunto viaja como cabecera: se rechaza, no se sanea;</item>
    /// <item>un destinatario (<c>para</c> o <c>cc</c>) con caracteres de control, aunque solo tenga espacios alrededor:
    /// <c>CORREO_INVALIDO</c>;</item>
    /// <item>más de <paramref name="maximoDestinatarios"/> destinatarios entre <c>para</c> y <c>cc</c> (sin contar la copia oculta al
    /// comercial): <c>CORREO_DEMASIADOS_DESTINATARIOS</c> con <c>detalle.maximo</c> y <c>detalle.recibidos</c>. El tope es
    /// <see cref="clsOpcionesCorreo.MaximoDestinatarios"/> (10 por defecto, <c>Correo__MaximoDestinatarios</c>): MailSender no tiene uno
    /// (<c>EmailRequestValidator</c> solo valida el formato de cada dirección), lo fija Cotifly;</item>
    /// <item>el formato estricto de cada dirección (<see cref="clsCabecerasCorreo.EsDireccionValida"/>) y, si <paramref name="exigePara"/>,
    /// al menos un <c>para</c>: <c>CORREO_INVALIDO</c>;</item>
    /// <item>asunto de más de <see cref="MaxAsunto"/> o cuerpo de más de <see cref="MaxCuerpo"/> caracteres: <c>CORREO_INVALIDO</c>.</item>
    /// </list>
    /// El cuerpo no es una cabecera (va escapado en el HTML), así que admite saltos de línea.
    /// </summary>
    public static clsErrorDominio? ValidarBorrador(
        IReadOnlyList<string>? para, IReadOnlyList<string>? cc, string? asunto, string? cuerpo, bool exigePara, int maximoDestinatarios)
    {
        if (clsCabecerasCorreo.TieneCaracteresDeControl(asunto))
        {
            return Invalido(MensajeAsuntoConControl);
        }

        if ((para ?? []).Concat(cc ?? []).Any(clsCabecerasCorreo.TieneCaracteresDeControl))
        {
            return Invalido(clsCorreoDestinatario.MensajeConCaracteresDeControl);
        }

        var limpiosPara = Limpiar(para);
        var limpiosCc = Limpiar(cc);
        var recibidos = limpiosPara.Count + limpiosCc.Count;
        if (recibidos > maximoDestinatarios)
        {
            return new clsErrorDominio(clsCodigosError.CorreoDemasiadosDestinatarios,
                $"El correo admite hasta {maximoDestinatarios} destinatarios entre 'para' y 'cc' (llegaron {recibidos}).")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["maximo"] = maximoDestinatarios, ["recibidos"] = recibidos },
            };
        }

        var destinatarios = exigePara || limpiosPara.Count > 0 ? ValidarDestinatarios(limpiosPara, limpiosCc) : ValidarFormato(limpiosCc, TipoDestinatario.Cc);
        if (destinatarios is not null)
        {
            return destinatarios;
        }

        var asuntoLimpio = string.IsNullOrWhiteSpace(asunto) ? null : asunto.Trim();
        var cuerpoLimpio = string.IsNullOrWhiteSpace(cuerpo) ? null : cuerpo.Trim();
        return asuntoLimpio is { Length: > MaxAsunto } ? Invalido($"El asunto supera {MaxAsunto} caracteres.")
            : cuerpoLimpio is { Length: > MaxCuerpo } ? Invalido($"El cuerpo supera {MaxCuerpo} caracteres.")
            : null;
    }

    private static clsErrorDominio? ValidarFormato(IReadOnlyList<string> emails, TipoDestinatario tipo) =>
        emails.Select(e => clsCorreoDestinatario.Crear(Guid.NewGuid(), e, tipo)).FirstOrDefault(d => d.EsFallo)?.Error;

    /// <summary>Destinatarios válidos según las reglas del dominio (<c>clsCorreoDestinatario</c>); al menos un <c>para</c>.</summary>
    public static clsErrorDominio? ValidarDestinatarios(IReadOnlyList<string> para, IReadOnlyList<string> cc)
    {
        ArgumentNullException.ThrowIfNull(para);
        ArgumentNullException.ThrowIfNull(cc);
        if (para.Count == 0)
        {
            return Invalido("El correo debe tener al menos un destinatario 'para'.");
        }

        foreach (var (email, tipo) in para.Select(p => (p, TipoDestinatario.Para)).Concat(cc.Select(c => (c, TipoDestinatario.Cc))))
        {
            var destinatario = clsCorreoDestinatario.Crear(Guid.NewGuid(), email, tipo);
            if (destinatario.EsFallo)
            {
                return destinatario.Error;
            }
        }

        return null;
    }

    private static List<string> Limpiar(IReadOnlyList<string>? emails) =>
        emails is null ? [] : [.. emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())];

    private static clsErrorDominio Invalido(string mensaje) => new(clsCorreo.CodigoError, mensaje);
}
