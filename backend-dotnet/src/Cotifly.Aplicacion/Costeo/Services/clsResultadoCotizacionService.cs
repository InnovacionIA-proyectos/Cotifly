using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Resultado ganada/perdida de la cotización en Nexo (HU-005.14, ADR-015 §6): <c>POST /web/cotizaciones/{folio}/resultado</c>.
/// Reemplaza al seguimiento interino (<see cref="IClsSeguimientoService"/>) para las cotizaciones que pasaron por Nexo.
/// </summary>
public interface IClsResultadoCotizacionService
{
    /// <summary>
    /// Dos escrituras: primero Nexo y, solo si responde 200, <c>ganada</c>, <c>motivoRechazo</c> y <c>marcadoAt</c> en Cotifly.
    /// Errores: <c>COTIZACION_NO_ENCONTRADA</c> (404), <c>SEGUIMIENTO_INVALIDO</c> (400), <c>COSTEO_NO_LISTO</c> (409: no está en
    /// Nexo), <c>RESULTADO_YA_DEFINIDO</c> y <c>COTIZACION_NO_ENTREGADA</c> (409, de Nexo, sin cambio local) y
    /// <c>COSTEO_NEXO_NO_DISPONIBLE</c>/<c>COSTEO_NEXO_TIEMPO_AGOTADO</c> (503, sin cambio local).
    /// </summary>
    Task<clsResultado<clsCotizacion>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsResultadoCotizacionService"/>.</summary>
/// <remarks>
/// <para><b><c>comentario</c></b> se reenvía a Nexo tal cual y no se persiste en Cotifly (decisión de la HU-005.14); solo
/// <c>motivo</c> queda en <c>motivoRechazo</c>. Límites de Nexo: <c>motivo</c> 100 y <c>comentario</c> 500 caracteres.</para>
/// <para><b>Entrega implícita.</b> Si Nexo entrega la cotización al marcar el resultado (lo hacía antes de su issue #171), la
/// respuesta trae <c>Entregada</c> y <c>estado_costeo</c> salta a <c>entregada</c> aunque estuviera en <c>autocosteo</c> o
/// <c>pendiente_costeo</c>: es un estado válido, no una inconsistencia.</para>
/// </remarks>
public sealed class clsResultadoCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsCosteoNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj) : IClsResultadoCotizacionService
{
    public const string HerramientaAuditoria = "resultado_cotizacion_nexo";
    public const int LargoMaximoMotivo = 100;
    public const int LargoMaximoComentario = 500;

    public async Task<clsResultado<clsCotizacion>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, CancellationToken cancellationToken = default)
    {
        var ganada = resultado?.Trim().ToLowerInvariant() switch
        {
            "ganada" => true,
            "perdida" => (bool?)false,
            _ => null,
        };
        if (ganada is null)
        {
            return Invalido("El campo 'resultado' es obligatorio y debe ser 'ganada' o 'perdida'.");
        }

        if (motivo is { Length: > LargoMaximoMotivo } || comentario is { Length: > LargoMaximoComentario })
        {
            return Invalido($"'motivo' admite máximo {LargoMaximoMotivo} caracteres y 'comentario' {LargoMaximoComentario} (límites de Nexo).");
        }

        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (cotizacion.ResultadoSoloLocal || cotizacion.CosteoId is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, cotizacion.ResultadoSoloLocal
                ? $"La cotización {cotizacion.Folio} no tiene resultado en Nexo (chat, descartada o costeo vencido): márcalo con el seguimiento de Cotifly."
                : $"La cotización {cotizacion.Folio} todavía no está en Nexo: reintenta el envío antes de marcar el resultado.");
        }

        // Nexo solo acepta el resultado de una cotización entregada: sin costeo completo no se le pregunta (y no se crea en
        // Nexo un borrador vacío solo para que lo rechace). En autocosteo sí se intenta: el polling pudo no ver aún la entrega.
        if (cotizacion.EstadoCosteo is not (EstadoCosteo.Entregada or EstadoCosteo.Autocosteo))
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEntregada,
                $"La cotización {cotizacion.Folio} todavía no tiene el costeo entregado por Nexo: no se puede marcar el resultado.");
        }

        var cronometro = Stopwatch.StartNew();
        var marcada = await MarcarEnNexoAsync(cotizacion, usuarioId, ganada.Value, motivo, comentario, cancellationToken);
        if (marcada.EsExitoso)
        {
            var ahora = reloj.AhoraUtc;
            cotizacion.MarcarSeguimiento(ganada, motivo, ahora);
            if (string.Equals(marcada.Valor.Estado, clsCotizacionNexo.EstadoEntregada, StringComparison.OrdinalIgnoreCase)
                && cotizacion.EstadoCosteo != EstadoCosteo.Entregada)
            {
                // Entrega implícita (o entrega que el polling aún no vio): válida, no una inconsistencia.
                cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, marcada.Valor.Id, ahora, siguiente: null);
            }

            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, resultado = ganada.Value ? "ganada" : "perdida", motivo, cotizacion_nexo_id = cotizacion.CotizacionNexoId },
            marcada.EsExitoso
                ? (object)new { estado_nexo = marcada.Valor.Estado, resultado_nexo = marcada.Valor.Resultado }
                : new { error = new { codigo = marcada.Error.Codigo, mensaje = marcada.Error.Mensaje } },
            marcada.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return marcada.EsExitoso ? cotizacion : marcada.Error;
    }

    private async Task<clsResultado<clsCotizacionNexo>> MarcarEnNexoAsync(
        clsCotizacion cotizacion, Guid usuarioId, bool ganada, string? motivo, string? comentario, CancellationToken cancellationToken)
    {
        var cotizacionNexoId = cotizacion.CotizacionNexoId;
        if (cotizacionNexoId is null)
        {
            // El polling todavía no la vio: "crear o recuperar" la cotización de Nexo de la solicitud.
            var recuperada = await nexo.ObtenerCotizacionAsync(usuarioId, cotizacion.CosteoId!, cancellationToken);
            if (recuperada.EsFallo)
            {
                return recuperada.Error;
            }

            cotizacionNexoId = recuperada.Valor.Id;
            cotizacion.RegistrarCotizacionNexo(cotizacionNexoId);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }

        return await nexo.MarcarResultadoAsync(
            usuarioId, cotizacionNexoId, new clsResultadoNexo(ganada, Normalizar(motivo), Normalizar(comentario)), cancellationToken);
    }

    private static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static clsErrorDominio Invalido(string mensaje) => new(clsSeguimientoService.CodigoSeguimientoInvalido, mensaje);
}
