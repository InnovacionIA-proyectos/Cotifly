using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>Qué pasó al consultar un correo de la agenda (para el log del worker y las pruebas).</summary>
public enum ResultadoConsultaCorreo
{
    /// <summary>Otra réplica (u otra vuelta) ya lo había reclamado: no se hizo nada.</summary>
    NoReclamado,

    /// <summary>Se consultó y sigue igual.</summary>
    SinCambio,

    /// <summary>Se consultó y cambió (estado, error, intentos o fecha de envío).</summary>
    Cambio,

    /// <summary>MailSender no respondió: se reprogramó.</summary>
    Error,

    /// <summary>Pasó la edad máxima sin confirmación de MailSender: quedó <c>fallido</c>.</summary>
    Vencido,
}

/// <summary>
/// Consulta del estado de los correos en MailSender (<c>GET /api/v1/emails/{id}</c>, ADR-023). El worker
/// (<c>clsWorkerEstadoCorreos</c>, mismo patrón que el polling del costeo) lee la agenda con <see cref="PendientesAsync"/> y procesa cada
/// correo con <see cref="ProcesarAsync"/> en su propio ámbito de DI.
/// </summary>
public interface IClsSeguimientoCorreosService
{
    /// <summary>Correos con la consulta vencida, hasta <c>Correo:Seguimiento:Lote</c>.</summary>
    Task<IReadOnlyList<clsConsultaCorreoPendiente>> PendientesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reclama el correo, consulta a MailSender, actualiza el estado y reprograma. Un fallo de MailSender nunca lanza.</summary>
    Task<ResultadoConsultaCorreo> ProcesarAsync(clsConsultaCorreoPendiente pendiente, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsSeguimientoCorreosService"/>.</summary>
/// <remarks>
/// Traducción de los estados de MailSender: <c>Queued</c>/<c>Draft</c> → <c>encolado</c>; <c>Processing</c>/<c>RetryScheduled</c> →
/// <c>enviando</c> (con el <c>lastError</c> del intento transitorio); <c>Sent</c> → <c>enviado</c>; <c>Failed</c>/<c>Cancelled</c> →
/// <c>fallido</c> con el <c>lastError</c>. Un 404 (MailSender ya no lo conoce) también es <c>fallido</c>. Pasada
/// <c>Correo:Seguimiento:EdadMaximaHoras</c> sin confirmación, <c>fallido</c> (se puede reintentar). Los reintentos de ENTREGA los hace
/// MailSender; aquí solo se consulta.
/// </remarks>
public sealed class clsSeguimientoCorreosService(
    IClsCorreoRepository correos,
    IClsSeguimientoCorreoExterno externo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IOptions<clsOpcionesCorreo> opciones,
    ILogger<clsSeguimientoCorreosService> logger) : IClsSeguimientoCorreosService
{
    /// <summary>Evento de auditoría cuando un correo llega a un estado final (<c>enviado</c> o <c>fallido</c>).</summary>
    public const string HerramientaEstadoCorreo = "estado_correo";

    public const string MensajeNoEncontrado =
        "MailSender ya no conoce este correo (404): no se puede confirmar ni reintentar. Envíalo de nuevo desde Cotifly.";

    public Task<IReadOnlyList<clsConsultaCorreoPendiente>> PendientesAsync(CancellationToken cancellationToken = default) =>
        correos.ListarConsultasPendientesAsync(reloj.AhoraUtc, opciones.Value.Seguimiento.Lote, cancellationToken);

    public async Task<ResultadoConsultaCorreo> ProcesarAsync(clsConsultaCorreoPendiente pendiente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pendiente);
        var config = opciones.Value.Seguimiento;
        var ahora = reloj.AhoraUtc;
        if (!await correos.ReclamarConsultaAsync(pendiente.Id, pendiente.SiguienteConsultaAt, ahora + config.Reclamo, cancellationToken))
        {
            return ResultadoConsultaCorreo.NoReclamado;
        }

        var correo = await correos.ObtenerAsync(pendiente.Id, cancellationToken);
        if (correo is null)
        {
            return ResultadoConsultaCorreo.NoReclamado;
        }

        if (!correo.EnCurso || correo.IdMailSender is not { } idExterno)
        {
            correo.ReprogramarConsulta(null);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            return ResultadoConsultaCorreo.SinCambio;
        }

        // La edad se cuenta desde la última aceptación (envío o reintento), no desde la creación.
        if (ahora - correo.InicioSeguimiento >= config.EdadMaxima)
        {
            correo.VencerConsulta(
                $"MailSender no confirmó la entrega en {config.EdadMaximaHoras} h (id {idExterno:D}): revisa el buzón o reintenta el envío.", ahora);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            await AuditarFinalAsync(correo, 0);
            return ResultadoConsultaCorreo.Vencido;
        }

        var cronometro = Stopwatch.StartNew();
        bool cambio;
        try
        {
            var estado = await externo.ConsultarAsync(idExterno, correo.Id.ToString("D"), cancellationToken);
            cambio = estado is null
                ? correo.ActualizarEntrega(EstadoCorreo.Fallido, MensajeNoEncontrado, correo.IntentosEntrega, correo.EnviadoAt, ahora, null)
                : correo.ActualizarEntrega(estado.Estado, estado.Error, estado.Intentos, estado.EnviadoAt, ahora, ahora + config.Intervalo);
        }
        catch (clsEnvioCorreoException ex)
        {
            // MailSender caído o lento: se vuelve a consultar más tarde; el estado no cambia.
            correo.ReprogramarConsulta(ahora + config.Intervalo + config.Intervalo);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            logger.LogWarning("No se pudo consultar en MailSender el correo {CorreoId} ({IdExterno}): {Motivo}", correo.Id, idExterno, ex.Message);
            return ResultadoConsultaCorreo.Error;
        }

        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        if (cambio && !correo.EnCurso)
        {
            await AuditarFinalAsync(correo, (int)cronometro.ElapsedMilliseconds);
        }

        return cambio ? ResultadoConsultaCorreo.Cambio : ResultadoConsultaCorreo.SinCambio;
    }

    private Task AuditarFinalAsync(clsCorreo correo, int duracionMs)
    {
        if (correo.Estado == EstadoCorreo.Fallido)
        {
            logger.LogWarning("El correo {CorreoId} de la cotización {CotizacionId} quedó fallido en MailSender: {Error}", correo.Id, correo.CotizacionId, correo.Error);
        }

        return auditoria.RegistrarAsync(
            HerramientaEstadoCorreo,
            new { correo_id = correo.Id, cotizacion_id = correo.CotizacionId, id_mailsender = correo.IdMailSender },
            new
            {
                estado = correo.Estado is { } e ? clsCodigoEnum.ACodigo(e) : null,
                error = correo.Error,
                intentos = correo.IntentosEntrega,
                enviado_at = correo.EnviadoAt,
            },
            correo.Estado == EstadoCorreo.Enviado,
            duracionMs,
            threadId: null);
    }
}
