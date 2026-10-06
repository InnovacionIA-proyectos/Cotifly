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
/// <c>enviar_cotizacion</c> de <c>orquestador/web.py</c>). Invariantes:
/// <list type="bullet">
/// <item>Ningún correo sale sin el comercial autenticado (<c>confirmado_por</c>), que además debe ser el dueño de la cotización.</item>
/// <item>Deduplicación: un envío exitoso a los mismos destinatarios <c>para</c> (sin distinguir mayúsculas ni orden) en los
/// últimos 5 minutos se rechaza; los fallidos no cuentan. La regla vive aquí, no en el repositorio (deuda de ADR-008), y se
/// mide con <see cref="IClsReloj"/>. Los envíos de una misma cotización se serializan (<see cref="clsCandadoEnvios"/>) y, dentro
/// del candado, la cotización se recarga de la base: dos envíos simultáneos a destinatarios distintos dejan <c>enviada</c> y luego
/// <c>reenviada</c>, no dos veces <c>enviada</c>.</item>
/// <item>El PDF sale del almacén; sin PDF → <c>CORREO_PDF_NO_ENCONTRADO</c>.</item>
/// <item>Remitente y <c>Reply-To</c>: el correo del comercial en <c>UsuariosWeb</c>.</item>
/// <item>Envío exitoso: fila <c>enviado</c> con sus destinatarios y <c>estadoEnvio</c> <c>sin_enviar</c> → <c>enviada</c> y
/// <c>enviada</c>/<c>reenviada</c> → <c>reenviada</c> (corrige el bug del Python, que siempre escribía <c>enviada</c>).</item>
/// <item>Fallo del transporte: fila <c>fallido</c> con el error, en una transacción propia (no hay otros cambios pendientes),
/// y <c>CORREO_ENVIO_FALLIDO</c>; el estado de envío no cambia.</item>
/// <item>Concurrencia (ADR-018 §12): antes de enviar, el estado de envío se RESERVA (enviada/reenviada, versión de negocio + 1)
/// con <c>GuardarCambiosSiVersionAsync</c> sobre la versión leída. Así un ajuste de margen o de conexos concurrente pierde
/// (<c>CONFLICTO_CONCURRENCIA</c> o <c>PDF_YA_ENVIADO</c>) y nunca invalida un PDF que ya está saliendo; si el ajuste ganó antes, el
/// envío responde <c>CONFLICTO_CONCURRENCIA</c> sin enviar nada. Si el transporte falla, la reserva se deshace.</item>
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

    public async Task<clsResultado<clsSalidaEnvioCorreo>> EnviarAsync(clsEntradaEnviarCorreo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (entrada.UsuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ConfirmacionRequerida, "Falta 'confirmado_por': el envío no está autorizado.");
        }

        if (!opciones.Value.Habilitado)
        {
            return new clsErrorDominio(clsCodigosError.CorreoDeshabilitado, "El envío de correo desde Cotifly está deshabilitado (Correo:Habilitado=false).");
        }

        var para = Limpiar(entrada.Para);
        var cc = Limpiar(entrada.Cc);
        var asunto = string.IsNullOrWhiteSpace(entrada.Asunto) ? null : entrada.Asunto.Trim();
        var cuerpo = string.IsNullOrWhiteSpace(entrada.Cuerpo) ? null : entrada.Cuerpo.Trim();
        var invalida = ValidarDestinatarios(para, cc)
                       ?? (asunto is { Length: > MaxAsunto } ? Invalido($"El asunto supera {MaxAsunto} caracteres.") : null)
                       ?? (cuerpo is { Length: > MaxCuerpo } ? Invalido($"El cuerpo supera {MaxCuerpo} caracteres.") : null);
        if (invalida is not null)
        {
            return invalida;
        }

        var cotizacion = await BuscarCotizacionAsync(entrada, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(entrada.UsuarioId, cancellationToken);

        using var turno = await candado.TomarAsync(cotizacion.Id, cancellationToken);

        // La cotización se leyó antes del candado: otro envío pudo cambiar estadoEnvio mientras tanto.
        await cotizaciones.RecargarAsync(cotizacion, cancellationToken);

        var recientes = await correos.ListarEnviadosDesdeAsync(cotizacion.Id, reloj.AhoraUtc - VentanaDuplicado, cancellationToken);
        if (recientes.Any(c => c.TieneMismosPara(para)))
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
        cuerpo ??= clsPlantillaCorreo.CuerpoPorDefecto;
        var mensaje = new clsMensajeCorreo(
            usuario?.Email,
            para,
            cc,
            asunto,
            cuerpo,
            clsPlantillaCorreo.Html(cuerpo),
            new clsAdjuntoCorreo(NombreAdjunto(cotizacion), pdf));

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

        string messageId;
        try
        {
            messageId = await transporte.EnviarAsync(mensaje, cancellationToken);
        }
        catch (clsEnvioCorreoException ex)
        {
            logger.LogWarning("Falló el envío de la cotización {Folio} por {Modo}: {Motivo}", cotizacion.Folio, transporte.Modo, ex.Message);
            await DeshacerReservaAsync(cotizacion, estadoAnterior);
            await RegistrarFallidoAsync(cotizacion, para, cc, asunto, entrada.UsuarioId, ex.Message);
            return new clsErrorDominio(clsCodigosError.CorreoEnvioFallido, ex.Message);
        }

        var registrado = await RegistrarEnviadoAsync(cotizacion, para, cc, asunto, entrada.UsuarioId, messageId);
        return new clsSalidaEnvioCorreo(Enviado: true, messageId, registrado);
    }

    private Task<clsCotizacion?> BuscarCotizacionAsync(clsEntradaEnviarCorreo entrada, CancellationToken cancellationToken)
    {
        if (entrada.CotizacionId is { } id && id != Guid.Empty)
        {
            return cotizaciones.ObtenerPorIdDelUsuarioAsync(id, entrada.UsuarioId, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(entrada.Folio)
            ? Task.FromResult<clsCotizacion?>(null)
            : cotizaciones.ObtenerPorFolioDelUsuarioAsync(entrada.Folio, entrada.UsuarioId, cancellationToken);
    }

    private async Task<bool> RegistrarEnviadoAsync(clsCotizacion cotizacion, IReadOnlyList<string> para, IReadOnlyList<string> cc, string asunto, Guid usuarioId, string messageId)
    {
        try
        {
            var correo = clsCorreo.Crear(cotizacion.Id, para, EstadoCorreo.Enviado, asunto, cc, usuarioId, messageId).Valor;
            correos.Agregar(correo);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None); // el estado de envío ya quedó en la reserva
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El correo ya salió: se informa como enviado para que el comercial no lo reenvíe.
            logger.LogError(ex, "El correo de la cotización {Folio} salió ({MessageId}) pero no se pudo registrar en la base.", cotizacion.Folio, messageId);
            return false;
        }
    }

    /// <summary>
    /// El correo no salió: vuelve al estado de envío anterior (y sube la versión, porque el estado cambió). Si otra escritura cambió la
    /// versión entretanto (el polling), recarga y reintenta; ningún ajuste puede colarse porque la cotización figura enviada.
    /// </summary>
    private async Task DeshacerReservaAsync(clsCotizacion cotizacion, EstadoEnvio? estadoAnterior)
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

    private async Task RegistrarFallidoAsync(clsCotizacion cotizacion, IReadOnlyList<string> para, IReadOnlyList<string> cc, string asunto, Guid usuarioId, string error)
    {
        try
        {
            var correo = clsCorreo.Crear(cotizacion.Id, para, EstadoCorreo.Fallido, asunto, cc, usuarioId, error: error).Valor;
            correos.Agregar(correo);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo registrar el envío fallido de la cotización {Folio}.", cotizacion.Folio);
        }
    }

    /// <summary>Nombre del adjunto: el del archivo guardado (<c>PCN-...pdf</c>) o, si no se puede leer de la referencia, el folio.</summary>
    public static string NombreAdjunto(clsCotizacion cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var nombre = string.IsNullOrWhiteSpace(cotizacion.PdfPath) ? null : Path.GetFileName(cotizacion.PdfPath.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(nombre) ? $"{cotizacion.Folio}.pdf" : nombre;
    }

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
