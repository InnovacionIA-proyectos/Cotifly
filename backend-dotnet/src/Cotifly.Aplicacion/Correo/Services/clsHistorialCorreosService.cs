using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// HU-033 (#199, ADR-023): historial de envíos de una cotización con el estado de MailSender
/// (<c>GET /web/cotizaciones/{folio}/correos</c>) y reintento de un envío fallido
/// (<c>POST /web/cotizaciones/{folio}/correos/{correoId}/reintentar</c>). Solo el dueño; una cotización ajena es 404.
/// </summary>
public interface IClsHistorialCorreosService
{
    /// <summary>Envíos de la cotización, del más reciente al más antiguo. <c>COTIZACION_NO_ENCONTRADA</c> si no es del comercial.</summary>
    Task<clsResultado<IReadOnlyList<clsEnvioCorreoHistorial>>> ListarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pide a MailSender que reintente un envío <c>fallido</c> (<c>POST /api/v1/emails/{id}/retry</c>): el correo vuelve a <c>encolado</c> y el
    /// worker sigue su estado. Errores: <c>COTIZACION_NO_ENCONTRADA</c>/<c>NO_ENCONTRADO</c> (404), <c>CORREO_DESHABILITADO</c> (503),
    /// <c>CORREO_NO_REINTENTABLE</c> (409: no está fallido, nunca llegó a MailSender o MailSender ya no lo conoce) y
    /// <c>CORREO_ENVIO_FALLIDO</c> (MailSender no respondió).
    /// </summary>
    Task<clsResultado<clsEnvioCorreoHistorial>> ReintentarAsync(Guid usuarioId, string folio, Guid correoId, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsHistorialCorreosService"/>.</summary>
public sealed class clsHistorialCorreosService(
    IClsCotizacionRepository cotizaciones,
    IClsCorreoRepository correos,
    IClsSeguimientoCorreoExterno externo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IOptions<clsOpcionesCorreo> opciones,
    ILogger<clsHistorialCorreosService> logger) : IClsHistorialCorreosService
{
    /// <summary>Evento de auditoría del reintento.</summary>
    public const string HerramientaReintentarCorreo = "reintentar_correo";

    public async Task<clsResultado<IReadOnlyList<clsEnvioCorreoHistorial>>> ListarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return NoEsTuya();
        }

        var lista = await correos.ListarPorCotizacionAsync(cotizacion.Id, cancellationToken);
        return clsResultado<IReadOnlyList<clsEnvioCorreoHistorial>>.Exito([.. lista.Select(Historial)]);
    }

    public async Task<clsResultado<clsEnvioCorreoHistorial>> ReintentarAsync(Guid usuarioId, string folio, Guid correoId, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return NoEsTuya();
        }

        if (!opciones.Value.Habilitado)
        {
            return new clsErrorDominio(clsCodigosError.CorreoDeshabilitado, "El envío de correo desde Cotifly está deshabilitado (Correo:Habilitado=false).");
        }

        var correo = correoId == Guid.Empty ? null : await correos.ObtenerAsync(correoId, cancellationToken);
        if (correo is null || correo.CotizacionId != cotizacion.Id)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoEncontrado, "Envío no encontrado en esta cotización.");
        }

        if (!correo.EsReintentable)
        {
            return new clsErrorDominio(clsCodigosError.CorreoNoReintentable, correo.Estado != EstadoCorreo.Fallido
                ? "Solo se reintenta un envío fallido: este todavía está en curso o ya salió."
                : "Este envío nunca llegó a MailSender (se rechazó al encolarlo): envíalo de nuevo.");
        }

        var cronometro = Stopwatch.StartNew();
        bool aceptado;
        try
        {
            aceptado = await externo.ReintentarAsync(correo.IdMailSender!.Value, IdCorrelacion(cotizacion.Folio, correo), cancellationToken);
        }
        catch (clsEnvioCorreoException ex)
        {
            logger.LogWarning("MailSender no aceptó el reintento del correo {CorreoId}: {Motivo}", correo.Id, ex.Message);
            await AuditarAsync(cotizacion.Folio, correo, exito: false, ex.Message, cronometro);
            return new clsErrorDominio(clsCodigosError.CorreoEnvioFallido, ex.Message);
        }

        var ahora = reloj.AhoraUtc;
        if (!aceptado)
        {
            correo.ActualizarEntrega(EstadoCorreo.Fallido, clsSeguimientoCorreosService.MensajeNoEncontrado, correo.IntentosEntrega, correo.EnviadoAt, ahora, null);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            await AuditarAsync(cotizacion.Folio, correo, exito: false, clsSeguimientoCorreosService.MensajeNoEncontrado, cronometro);
            return new clsErrorDominio(clsCodigosError.CorreoNoReintentable, clsSeguimientoCorreosService.MensajeNoEncontrado);
        }

        correo.RegistrarReintento(ahora, ahora + opciones.Value.Seguimiento.Intervalo);
        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        await AuditarAsync(cotizacion.Folio, correo, exito: true, null, cronometro);
        return Historial(correo);
    }

    /// <summary>Fila del historial: los destinatarios originales y el estado que reporta MailSender.</summary>
    public static clsEnvioCorreoHistorial Historial(clsCorreo correo)
    {
        ArgumentNullException.ThrowIfNull(correo);
        return new clsEnvioCorreoHistorial(
            correo.Id,
            correo.CreatedAt,
            clsCodigoEnum.ACodigo(correo.Estado ?? EstadoCorreo.Enviado),
            correo.Para,
            correo.Cc,
            correo.Asunto,
            correo.Error,
            correo.IntentosEntrega,
            correo.EnviadoAt,
            correo.EstadoActualizadoAt,
            correo.RedirigidoA,
            correo.IdMailSender?.ToString("D"),
            correo.EsReintentable);
    }

    private static string IdCorrelacion(string folio, clsCorreo correo) => clsCorreoService.IdCorrelacion(folio, correo.Id);

    private Task AuditarAsync(string folio, clsCorreo correo, bool exito, string? error, Stopwatch cronometro) =>
        auditoria.RegistrarAsync(
            HerramientaReintentarCorreo,
            new { folio, correo_id = correo.Id, id_mailsender = correo.IdMailSender },
            exito ? new { estado = clsCodigoEnum.ACodigo(correo.Estado ?? EstadoCorreo.Encolado) } : new { error },
            exito,
            (int)cronometro.ElapsedMilliseconds,
            threadId: null);

    private static clsErrorDominio NoEsTuya() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
