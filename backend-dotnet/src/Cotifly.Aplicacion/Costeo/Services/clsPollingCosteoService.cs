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
    IClsPrecioCosteoService precioCosteo,
    IClsEnvioConexosNexoService? envioConexos = null,
    IClsSituacionLineasNexo? situacionesNexo = null,
    IClsSincronizacionResultadoNexoService? sincronizacion = null) : IClsPollingCosteoService
{
    /// <summary>Evento de auditoría de cada cambio de estado detectado (<c>EventosHerramienta</c>).</summary>
    public const string HerramientaCambioEstado = "costeo_nexo_estado";

    /// <summary>Evento de auditoría de una consulta fallida.</summary>
    public const string HerramientaErrorConsulta = "costeo_nexo_consulta";

    /// <summary>
    /// HU-036.4 (#208): evento de auditoría cuando Nexo trae otro costo para una cotización finalizada (el precio se conserva): lo
    /// guardado y lo de Nexo por camión, con su ruta.
    /// </summary>
    public const string HerramientaPrecioCongelado = "costeo_nexo_precio_congelado";

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
        // ADR-019 (Riesgo 2): "en Nexo" sin el id de la solicitud (migrada sin respaldo) nadie la puede consultar. Queda en error_envio
        // con el motivo SOLICITUD_NEXO_DESCONOCIDA: desde ahí funcionan el reintento, la re-solicitud y la perdida solo en Cotifly.
        if (!cotizacion.PerdidaSoloEnCotifly && cotizacion.SolicitudNexoDesconocida)
        {
            var anteriorDesconocida = cotizacion.EstadoCosteo;
            var versionDesconocida = cotizacion.VersionNegocio;
            cotizacion.MarcarSolicitudNexoDesconocida();
            if (!await GuardarConVersionAsync(cotizacion, versionDesconocida, cambioNegocio: true))
            {
                return ResultadoConsultaCosteo.Conflicto;
            }

            await AuditarCambioAsync(cotizacion, anteriorDesconocida, 0);
            return ResultadoConsultaCosteo.Detenida;
        }

        var relectura = cotizacion.RequiereRelecturaCosteo;

        // HU-033 (#199): un resultado pendiente de sincronizar en una cotización que ya no se costea. Entregada (y con precio): solo se
        // envía el resultado a Nexo. Descartada, vencida o sin envío: ya no se podrá enviar y queda solo en Cotifly.
        if (sincronizacion is not null && cotizacion.ResultadoPendienteDeSincronizar && !relectura && !cotizacion.SeConsultaEnNexo)
        {
            var versionSincronizacion = cotizacion.VersionNegocio;
            var cambioSincronizacion = cotizacion.EstadoCosteo == EstadoCosteo.Entregada
                ? await sincronizacion.SincronizarAsync(cotizacion, ahora, cancellationToken)
                : sincronizacion.Abandonar(cotizacion, MotivoNoSincronizable(cotizacion));
            // La agenda queda en el reclamo (o en el reintento que programó la sincronización): la siguiente vuelta ya no entra aquí y
            // decide como cualquier otra (reintento de conexos o detenerse).
            return !await GuardarConVersionAsync(cotizacion, versionSincronizacion, cambioSincronizacion) ? ResultadoConsultaCosteo.Conflicto
                : cambioSincronizacion ? ResultadoConsultaCosteo.Cambio
                : ResultadoConsultaCosteo.SinCambio;
        }

        // Decisión 10 (HU-021.9): una cotización que ya no se consulta (entregada en la misma consulta en que falló el envío de conexos)
        // vuelve a la agenda SOLO para reintentar ese envío: no se vuelve a costear ni se tocan montos ni la versión de negocio.
        if (!(cotizacion.SeConsultaEnNexo || relectura) && !cotizacion.PerdidaSoloEnCotifly && envioConexos is not null
            && cotizacion.UsuarioId is { } duenoConexos && cotizacion.CotizacionNexoId is not null
            && await envioConexos.TieneReintentosAsync(cotizacion, cancellationToken))
        {
            var reintento = await envioConexos.ReintentarAsync(cotizacion, duenoConexos, CancellationToken.None);
            if (reintento.PorReintentar > 0)
            {
                // Con los conexos en espera (Nexo sin responder antes del POST) el reintento se espacia 5, 15 y 60 min (#136).
                cotizacion.ProgramarRelecturaCosteo(ahora + (reintento.EsperaReintento ?? config.IntervaloReintentoConexos));
            }
            else
            {
                cotizacion.DetenerPollingCosteo();
            }

            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            return reintento.PorReintentar > 0 ? ResultadoConsultaCosteo.SinCambio : ResultadoConsultaCosteo.Detenida;
        }

        if (!(cotizacion.SeConsultaEnNexo || relectura) || cotizacion.PerdidaSoloEnCotifly || cotizacion.UsuarioId is not { } usuarioId || cotizacion.CosteoEnviadoAt is not { } enviadaAt)
        {
            cotizacion.DetenerPollingCosteo();
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            return ResultadoConsultaCosteo.Detenida;
        }

        var anterior = cotizacion.EstadoCosteo;
        var version = cotizacion.VersionNegocio;
        // HU-033 (#199): con un resultado pendiente de sincronizar se sigue esperando la entrega de la Mesa más allá de la edad máxima del
        // costeo, hasta CosteoNexo:EdadMaximaSincronizacionResultadoDias; pasado ese plazo el resultado queda solo en Cotifly.
        var esperaSincronizacion = sincronizacion is not null && cotizacion.ResultadoPendienteDeSincronizar
                                   && ahora - enviadaAt < config.EdadMaximaSincronizacionResultado;
        if (!relectura && !esperaSincronizacion && clsAgendaPollingCosteo.Vencida(ahora, enviadaAt, config))
        {
            var abandonado = sincronizacion?.Abandonar(cotizacion,
                $"Nexo no entregó la cotización en {config.EdadMaximaSincronizacionResultadoDias} días: el resultado queda solo en Cotifly.") ?? false;
            var vencio = cotizacion.VencerPollingCosteo(
                $"Nexo no costeó la solicitud en {config.EdadMaximaHoras} h: se dejó de consultar. Revísala con el equipo de Nexo o envía una versión nueva.");
            if (!await GuardarConVersionAsync(cotizacion, version, vencio || abandonado))
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
            cambioDesconocida |= sincronizacion?.Abandonar(cotizacion,
                $"Nexo ya no reconoce la solicitud {cotizacion.SolicitudNexoId}: el resultado queda solo en Cotifly.") ?? false;
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
            if (sincronizacion is not null && cotizacion.ResultadoPendienteDeSincronizar && cotizacion.CosteoSiguienteConsultaAt is null)
            {
                // HU-033: pasada la edad máxima del costeo, un resultado pendiente sigue esperando la entrega (Nexo caído no la detiene).
                cotizacion.ProgramarRelecturaCosteo(ahora + config.IntervaloMaximo);
            }

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
        // aunque la cotización ya tenga resultado ganada/perdida (HU-013.14): solo el PDF y (HU-036.4, #208) la finalización congelan el
        // precio.
        // HU-021.8 (#135): con conexos pendientes de leer, la ruta pedida y los conexos que Nexo ya tenía antes del POST.
        var pendientes = cotizacion.ConexosPendientesDeLeer
            ? await cotizaciones.ObtenerConexosPendientesNexoAsync(cotizacion.Folio, CancellationToken.None)
            : null;
        var motivoAntes = cotizacion.MotivoEstadoCosteo;
        var cambio = clsLecturaCosteo.AplicarFoto(cotizacion, consulta.Valor, ahora, config, precioCosteo, pendientes);

        // HU-036.4 (#208): con la cotización finalizada el precio no cambia; lo que trajo Nexo se audita (una vez por aviso nuevo).
        var congelados = cotizacion.Finalizada && !string.Equals(motivoAntes, cotizacion.MotivoEstadoCosteo, StringComparison.Ordinal)
            ? clsPrecioCosteoService.CambiosConPrecioCongelado(cotizacion, consulta.Valor)
            : Array.Empty<clsCambioCosteoCongelado>();

        // Una relectura pasada la edad máxima que sigue sin costeo se vence ahora, después de leerla (HU-013.14).
        if (relectura && cotizacion.EstadoCosteo == EstadoCosteo.PendienteCosteo && clsAgendaPollingCosteo.Vencida(ahora, enviadaAt, config))
        {
            cambio |= cotizacion.VencerPollingCosteo(
                $"Nexo no costeó la solicitud en {config.EdadMaximaHoras} h: se dejó de consultar. Revísala con el equipo de Nexo o envía una versión nueva.");
        }
        // HU-033 (#199): con el resultado pendiente, la agenda no se apaga por la edad máxima del costeo mientras Nexo no entregue; si Nexo
        // ya descartó o venció la solicitud, el resultado queda solo en Cotifly.
        if (sincronizacion is not null && cotizacion.ResultadoPendienteDeSincronizar)
        {
            if (cotizacion.EstadoCosteo is EstadoCosteo.Descartada or EstadoCosteo.CosteoVencido)
            {
                cambio |= sincronizacion.Abandonar(cotizacion, MotivoNoSincronizable(cotizacion));
            }
            else if (cotizacion.SeConsultaEnNexo && cotizacion.CosteoSiguienteConsultaAt is null)
            {
                cotizacion.ProgramarRelecturaCosteo(ahora + config.IntervaloMaximo);
            }
        }

        if (!await GuardarConVersionAsync(cotizacion, version, cambio))
        {
            // El precio se calculó con un margen que el comercial pudo cambiar entretanto: se descarta y se repite después.
            logger.LogInformation("La cotización {Folio} cambió mientras se consultaba a Nexo: se vuelve a consultar en el siguiente ciclo.", cotizacion.Folio);
            return ResultadoConsultaCosteo.Conflicto;
        }

        // #181: lo que Nexo dice de la línea de cada camión (estado y motivo pendiente) queda a la vista en GET .../costeo, sin guardarse en
        // la base. Solo con la foto ya guardada: así coincide con los camiones asociados que ve el comercial.
        situacionesNexo?.Registrar(cotizacion.Id, clsLecturaCosteo.SituacionesPorCamion(cotizacion, consulta.Valor));

        if (cambio)
        {
            await AuditarCambioAsync(cotizacion, anterior, (int)cronometro.ElapsedMilliseconds);
        }

        if (congelados.Count > 0)
        {
            await AuditarPrecioCongeladoAsync(cotizacion, congelados, (int)cronometro.ElapsedMilliseconds);
        }

        // HU-033 (#199): la Mesa entregó y hay un resultado pendiente: se envía a Nexo una sola vez (escritura versionada aparte).
        if (sincronizacion is not null && cotizacion.ResultadoPendienteDeSincronizar && cotizacion.EstadoCosteo == EstadoCosteo.Entregada)
        {
            var versionSincronizacion = cotizacion.VersionNegocio;
            var cambioSincronizacion = await sincronizacion.SincronizarAsync(cotizacion, ahora, CancellationToken.None);
            // Se guarda siempre: aunque no cambie nada visible, la sincronización pudo reprogramar la agenda (reintento).
            if (!await GuardarConVersionAsync(cotizacion, versionSincronizacion, cambioSincronizacion))
            {
                logger.LogInformation("La cotización {Folio} cambió mientras se enviaba el resultado pendiente: se reintenta en el siguiente ciclo.", cotizacion.Folio);
            }
        }

        // Decisión 10 (HU-021.9): con la cotización de Nexo ya leída (y la línea del primer camión de cada ruta emparejada), los conexos
        // de las rutas que todavía no están en Nexo se envían una sola vez. Un fallo no frena el costeo: queda en el log.
        if (envioConexos is not null && consulta.Valor.Cotizacion is { } cotizacionNexo)
        {
            try
            {
                var envio = await envioConexos.EnviarPendientesAsync(cotizacion, cotizacionNexo, usuarioId, CancellationToken.None);

                // Si el polling se detuvo en esta consulta (entregada, descartada o vencida) y quedaron conexos por reintentar, la
                // cotización se vuelve a agendar solo para el envío (hasta CosteoNexo:MaximoReintentosConexos).
                if (envio.PorReintentar > 0 && cotizacion.CosteoSiguienteConsultaAt is null)
                {
                    cotizacion.ProgramarRelecturaCosteo(ahora + (envio.EsperaReintento ?? config.IntervaloReintentoConexos));
                    await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "No se pudieron enviar a Nexo los conexos de {Folio}: se reintenta en el siguiente ciclo.", cotizacion.Folio);
            }
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

    /// <summary>HU-033: por qué un resultado pendiente ya no se podrá enviar a Nexo, según el estado de costeo.</summary>
    private static string MotivoNoSincronizable(clsCotizacionSolicitud cotizacion) => cotizacion.EstadoCosteo switch
    {
        EstadoCosteo.Descartada => "Nexo descartó la solicitud: el resultado queda solo en Cotifly.",
        EstadoCosteo.CosteoVencido => "El costeo en Nexo venció sin entrega: el resultado queda solo en Cotifly.",
        _ => "La cotización ya no está en el costeo de Nexo: el resultado queda solo en Cotifly.",
    };

    /// <summary>HU-036.4 (#208): el costo que trajo Nexo para una cotización finalizada, frente al guardado, por camión y ruta.</summary>
    private Task AuditarPrecioCongeladoAsync(clsCotizacionSolicitud cotizacion, IReadOnlyList<clsCambioCosteoCongelado> cambios, int duracionMs) =>
        auditoria.RegistrarAsync(
            HerramientaPrecioCongelado,
            new { folio = cotizacion.Folio, costeo_id = cotizacion.SolicitudNexoId, finalizada_at = cotizacion.FinalizadaAt },
            new
            {
                estado_costeo = cotizacion.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null,
                precio_final = cotizacion.PrecioFinal,
                motivo = cotizacion.MotivoEstadoCosteo,
                camiones = cambios.Select(c => new
                {
                    linea_orden = c.Ruta.Orden,
                    linea_solicitud_id = c.Ruta.Id,
                    camion_id = c.CamionId,
                    flete_guardado = c.FleteGuardado,
                    flete_nexo = c.FleteNexo,
                    retorno_guardado = c.RetornoGuardado,
                    retorno_nexo = c.RetornoNexo,
                    rechazado = c.Rechazado,
                }).ToList(),
            },
            exito: true,
            duracionMs,
            cotizacion.ThreadId);

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
