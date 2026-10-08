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

    /// <summary>
    /// Otra escritura (un ajuste del comercial, otra réplica) cambió la versión de negocio de la cotización mientras se consultaba a
    /// Nexo: no se guardó nada y se vuelve a consultar en el siguiente ciclo, cuando vence el reclamo (ADR-018 §12).
    /// </summary>
    Conflicto,

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

    /// <summary>
    /// Reparación de HU-013.14 (ADR-018 §17): agenda una relectura de las cotizaciones que quedaron en <c>entregada</c> sin precio, sin
    /// PDF, sin motivo y sin agenda (el resultado se registró antes de que Cotifly leyera el costeo). El worker la llama al arrancar;
    /// es idempotente. Devuelve cuántas agendó.
    /// </summary>
    Task<int> ReprogramarEntregadasSinPrecioAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsPollingCosteoService"/>.</summary>
public sealed class clsPollingCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsCosteoNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IOptions<clsOpcionesCosteoNexo> opciones,
    ILogger<clsPollingCosteoService> logger,
    IClsPrecioCosteoService precioCosteo) : IClsPollingCosteoService
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

        // HU-013.14: una entregada sin precio, o una con resultado cuyo costo no se leyó, se lee aunque sea terminal o haya pasado la
        // edad máxima. Una perdida solo en Cotifly (HU-013.17) ya no se consulta.
        var relectura = cotizacion.RequiereRelecturaCosteo;
        if (!(cotizacion.SeConsultaEnNexo || relectura) || cotizacion.PerdidaSoloEnCotifly || cotizacion.UsuarioId is not { } usuarioId || cotizacion.CosteoEnviadoAt is not { } enviadaAt)
        {
            cotizacion.DetenerPollingCosteo();
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            return ResultadoConsultaCosteo.Detenida;
        }

        var anterior = cotizacion.EstadoCosteo;
        var version = cotizacion.VersionNegocio;
        if (!relectura && clsAgendaPollingCosteo.Vencida(ahora, enviadaAt, config))
        {
            var vencio = cotizacion.VencerPollingCosteo(
                $"Nexo no costeó la solicitud en {config.EdadMaximaHoras} h: se dejó de consultar. Revísala con el equipo de Nexo o envía una versión nueva.");
            if (!await GuardarConVersionAsync(cotizacion, version, vencio))
            {
                return ResultadoConsultaCosteo.Conflicto;
            }

            if (vencio)
            {
                await AuditarCambioAsync(cotizacion, anterior, 0);
            }

            return ResultadoConsultaCosteo.Detenida;
        }

        var cronometro = Stopwatch.StartNew();
        var consulta = await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.SolicitudNexoId!, cancellationToken);
        if (consulta.EsFallo && consulta.Error.Codigo == clsCodigosError.CosteoNexoSolicitudNoEncontrada)
        {
            var cambioDesconocida = cotizacion.DetenerPorSolicitudDesconocida(
                $"Nexo ya no reconoce la solicitud {cotizacion.SolicitudNexoId} (404 SOLICITUD_NO_ENCONTRADA): se dejó de consultar. Revísala con el equipo de Nexo o envía una versión nueva.");
            cotizacion.RegistrarFalloConsultaCosteo(siguiente: null);
            if (!await GuardarConVersionAsync(cotizacion, version, cambioDesconocida))
            {
                return ResultadoConsultaCosteo.Conflicto;
            }

            await auditoria.RegistrarAsync(
                HerramientaErrorConsulta,
                new { folio = cotizacion.Folio, costeo_id = cotizacion.SolicitudNexoId, terminal = true },
                new { error = new { codigo = consulta.Error.Codigo, mensaje = consulta.Error.Mensaje, detalle = consulta.Error.Detalle } },
                exito: false,
                (int)cronometro.ElapsedMilliseconds,
                cotizacion.ThreadId);
            if (cambioDesconocida)
            {
                await AuditarCambioAsync(cotizacion, anterior, (int)cronometro.ElapsedMilliseconds);
            }

            return ResultadoConsultaCosteo.Detenida;
        }

        if (consulta.EsFallo)
        {
            var fallos = cotizacion.CosteoConsultasFallidas + 1;
            // La relectura sigue con espaciado aunque haya pasado la edad máxima, hasta CosteoNexo:MaximoFallosRelectura (HU-013.14).
            cotizacion.RegistrarFalloConsultaCosteo(relectura
                ? ahora + clsAgendaPollingCosteo.Espera(ahora, enviadaAt, fallos, config)
                : clsAgendaPollingCosteo.Siguiente(ahora, enviadaAt, fallos, config));
            var agotada = relectura && fallos >= config.MaximoFallosRelectura;
            var cambioAgotada = false;
            if (agotada)
            {
                cotizacion.DetenerPollingCosteo();
                var motivo = $"No se pudo leer el costeo en Nexo tras {fallos} intentos ({consulta.Error.Codigo}): se dejó de consultar sin precio. "
                    + "Revísala con el equipo de Nexo.";
                cambioAgotada = clsAgendaPollingCosteo.Vencida(ahora, enviadaAt, config) && cotizacion.VencerPollingCosteo(motivo);
                if (!cambioAgotada)
                {
                    cotizacion.AdvertirCosteo(motivo);
                }

                }

            if (agotada)
            {
                if (!await GuardarConVersionAsync(cotizacion, version, cambioNegocio: true))
                {
                    return ResultadoConsultaCosteo.Conflicto;
                }
            }
            else
            {
                await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            }

            logger.LogWarning("Consulta del costeo en Nexo fallida para {Folio} ({Codigo}); fallos seguidos: {Fallos}.",
                cotizacion.Folio, consulta.Error.Codigo, fallos);
            await auditoria.RegistrarAsync(
                HerramientaErrorConsulta,
                new { folio = cotizacion.Folio, costeo_id = cotizacion.SolicitudNexoId, fallos_seguidos = fallos },
                new { error = new { codigo = consulta.Error.Codigo, mensaje = consulta.Error.Mensaje, detalle = consulta.Error.Detalle } },
                exito: false,
                (int)cronometro.ElapsedMilliseconds,
                cotizacion.ThreadId);
            return agotada ? ResultadoConsultaCosteo.Detenida : ResultadoConsultaCosteo.Error;
        }

        // ADR-017 y HU-021: el estado y el costo se derivan por camión, el precio por ruta y el total en la solicitud. El precio se aplica
        // aunque la cotización ya tenga resultado ganada/perdida (HU-013.14): solo el PDF congela el precio.
        var cambio = clsLecturaCosteo.AplicarFoto(cotizacion, consulta.Valor, ahora, config, precioCosteo);

        // Una relectura pasada la edad máxima que sigue sin costeo se vence ahora, después de leerla (HU-013.14).
        if (relectura && cotizacion.EstadoCosteo == EstadoCosteo.PendienteCosteo && clsAgendaPollingCosteo.Vencida(ahora, enviadaAt, config))
        {
            cambio |= cotizacion.VencerPollingCosteo(
                $"Nexo no costeó la solicitud en {config.EdadMaximaHoras} h: se dejó de consultar. Revísala con el equipo de Nexo o envía una versión nueva.");
        }
        if (!await GuardarConVersionAsync(cotizacion, version, cambio))
        {
            // El precio se calculó con un margen que el comercial pudo cambiar entretanto: se descarta y se repite después.
            logger.LogInformation("La cotización {Folio} cambió mientras se consultaba a Nexo: se vuelve a consultar en el siguiente ciclo.", cotizacion.Folio);
            return ResultadoConsultaCosteo.Conflicto;
        }

        if (cambio)
        {
            await AuditarCambioAsync(cotizacion, anterior, (int)cronometro.ElapsedMilliseconds);
        }

        return cambio ? ResultadoConsultaCosteo.Cambio : ResultadoConsultaCosteo.SinCambio;
    }

    public async Task<int> ReprogramarEntregadasSinPrecioAsync(CancellationToken cancellationToken = default)
    {
        var agendadas = await cotizaciones.ReprogramarEntregadasSinPrecioAsync(reloj.AhoraUtc, cancellationToken);
        if (agendadas > 0)
        {
            logger.LogWarning("{Agendadas} cotizaciones entregadas sin precio quedaron agendadas para releer su costeo en Nexo (HU-013.14).", agendadas);
        }

        return agendadas;
    }

    /// <summary>
    /// Guarda solo si nadie cambió la versión de negocio desde que se cargó la cotización (ADR-018 §12). Sube la versión cuando cambia
    /// el estado, el costo o el precio (<paramref name="cambioNegocio"/>); el reclamo y la agenda no la cambian.
    /// </summary>
    private async Task<bool> GuardarConVersionAsync(clsCotizacionSolicitud cotizacion, int version, bool cambioNegocio)
    {
        if (cambioNegocio)
        {
            cotizacion.IncrementarVersionNegocio();
        }

        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None);
    }

    private Task AuditarCambioAsync(clsCotizacionSolicitud cotizacion, EstadoCosteo? anterior, int duracionMs) =>
        auditoria.RegistrarAsync(
            HerramientaCambioEstado,
            new { folio = cotizacion.Folio, costeo_id = cotizacion.SolicitudNexoId, estado_anterior = anterior is { } a ? clsCodigoEnum.ACodigo(a) : null },
            new
            {
                estado_costeo = cotizacion.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null,
                cotizacion_nexo_id = cotizacion.CotizacionNexoId,
                motivo = cotizacion.MotivoEstadoCosteo,
                precio_final = cotizacion.PrecioFinal,
            },
            exito: true,
            duracionMs,
            cotizacion.ThreadId);
}
