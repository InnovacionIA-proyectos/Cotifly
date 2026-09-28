using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>Qué pasó al procesar una cotización de la agenda del polling (para el log del worker y las pruebas).</summary>
public enum ResultadoConsultaCosteo
{
    /// <summary>Otra réplica (u otra vuelta) ya la había reclamado: no se hizo nada.</summary>
    NoReclamada,

    /// <summary>Se consultó y el estado sigue igual.</summary>
    SinCambio,

    /// <summary>Se consultó y el estado cambió (queda en la auditoría).</summary>
    Cambio,

    /// <summary>La consulta a Nexo falló: se reprogramó con espaciado y quedó el error en la auditoría.</summary>
    Error,

    /// <summary>Ya no se consulta (estado que no se sigue, datos incompletos o edad máxima).</summary>
    Detenida,
}

/// <summary>
/// Polling del costeo en Nexo (HU-005.6, ADR-015 §4). El worker (<c>clsWorkerCosteoNexo</c>) lee la agenda con
/// <see cref="PendientesAsync"/> y procesa cada cotización con <see cref="ProcesarAsync"/> en su propio ámbito de DI.
/// </summary>
public interface IClsPollingCosteoService
{
    /// <summary>Cotizaciones con la consulta vencida, hasta <c>CosteoNexo:Lote</c>.</summary>
    Task<IReadOnlyList<clsConsultaCosteoPendiente>> PendientesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclama la cotización, consulta a Nexo, deriva <c>estado_costeo</c> y reprograma. Idempotente: procesar dos veces la misma
    /// foto de Nexo no cambia nada; un fallo de Nexo nunca lanza.
    /// </summary>
    Task<ResultadoConsultaCosteo> ProcesarAsync(clsConsultaCosteoPendiente pendiente, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsPollingCosteoService"/>.</summary>
public sealed class clsPollingCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsCosteoNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IOptions<clsOpcionesCosteoNexo> opciones,
    ILogger<clsPollingCosteoService> logger) : IClsPollingCosteoService
{
    /// <summary>Evento de auditoría de cada cambio de estado detectado (<c>EventosHerramienta</c>).</summary>
    public const string HerramientaCambioEstado = "costeo_nexo_estado";

    /// <summary>Evento de auditoría de una consulta fallida.</summary>
    public const string HerramientaErrorConsulta = "costeo_nexo_consulta";

    public Task<IReadOnlyList<clsConsultaCosteoPendiente>> PendientesAsync(CancellationToken cancellationToken = default) =>
        cotizaciones.ListarConsultasCosteoPendientesAsync(reloj.AhoraUtc, opciones.Value.Lote, cancellationToken);

    public async Task<ResultadoConsultaCosteo> ProcesarAsync(clsConsultaCosteoPendiente pendiente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pendiente);
        var config = opciones.Value;
        var ahora = reloj.AhoraUtc;
        if (!await cotizaciones.ReclamarConsultaCosteoAsync(pendiente.Id, pendiente.SiguienteConsultaAt, ahora + config.Reclamo, cancellationToken))
        {
            return ResultadoConsultaCosteo.NoReclamada;
        }

        var cotizacion = await cotizaciones.ObtenerPorIdAsync(pendiente.Id, cancellationToken);
        if (cotizacion is null)
        {
            return ResultadoConsultaCosteo.NoReclamada;
        }

        if (!cotizacion.SeConsultaEnNexo || cotizacion.UsuarioId is not { } usuarioId || cotizacion.CosteoEnviadoAt is not { } enviadaAt)
        {
            cotizacion.DetenerPollingCosteo();
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            return ResultadoConsultaCosteo.Detenida;
        }

        var anterior = cotizacion.EstadoCosteo;
        if (clsAgendaPollingCosteo.Vencida(ahora, enviadaAt, config))
        {
            var vencio = cotizacion.VencerPollingCosteo(
                $"Nexo no costeó la solicitud en {config.EdadMaximaHoras} h: se dejó de consultar. Revísala con el equipo de Nexo o envía una versión nueva.");
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            if (vencio)
            {
                await AuditarCambioAsync(cotizacion, anterior, 0);
            }

            return ResultadoConsultaCosteo.Detenida;
        }

        var cronometro = Stopwatch.StartNew();
        var consulta = await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.CosteoId!, cancellationToken);
        if (consulta.EsFallo)
        {
            var fallos = cotizacion.CosteoConsultasFallidas + 1;
            cotizacion.RegistrarFalloConsultaCosteo(ahora, clsAgendaPollingCosteo.Siguiente(ahora, enviadaAt, fallos, config));
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            logger.LogWarning("Consulta del costeo en Nexo fallida para {Folio} ({Codigo}); fallos seguidos: {Fallos}.",
                cotizacion.Folio, consulta.Error.Codigo, fallos);
            await auditoria.RegistrarAsync(
                HerramientaErrorConsulta,
                new { folio = cotizacion.Folio, costeo_id = cotizacion.CosteoId, fallos_seguidos = fallos },
                new { error = new { codigo = consulta.Error.Codigo, mensaje = consulta.Error.Mensaje, detalle = consulta.Error.Detalle } },
                exito: false,
                (int)cronometro.ElapsedMilliseconds,
                cotizacion.ThreadId);
            return ResultadoConsultaCosteo.Error;
        }

        var foto = consulta.Valor;
        var estado = clsDerivacionEstadoCosteo.Derivar(foto);
        var siguiente = clsDerivacionEstadoCosteo.EsTerminal(estado) ? null : clsAgendaPollingCosteo.Siguiente(ahora, enviadaAt, 0, config);
        var cambio = cotizacion.RegistrarConsultaCosteo(estado, foto.Cotizacion?.Id, ahora, siguiente);
        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        if (cambio)
        {
            await AuditarCambioAsync(cotizacion, anterior, (int)cronometro.ElapsedMilliseconds);
        }

        return cambio ? ResultadoConsultaCosteo.Cambio : ResultadoConsultaCosteo.SinCambio;
    }

    private Task AuditarCambioAsync(clsCotizacion cotizacion, EstadoCosteo? anterior, int duracionMs) =>
        auditoria.RegistrarAsync(
            HerramientaCambioEstado,
            new { folio = cotizacion.Folio, costeo_id = cotizacion.CosteoId, estado_anterior = anterior is { } a ? clsCodigoEnum.ACodigo(a) : null },
            new
            {
                estado_costeo = cotizacion.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null,
                cotizacion_nexo_id = cotizacion.CotizacionNexoId,
                motivo = cotizacion.MotivoEstadoCosteo,
            },
            exito: true,
            duracionMs,
            cotizacion.ThreadId);
}
