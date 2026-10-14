using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>Qué pasó al procesar una ruta de la cola de la cubicación en segundo plano (para el log del procesador y las pruebas).</summary>
public enum ResultadoCubicacionRuta
{
    /// <summary>No había cupo libre en el cubicador (P6): la ruta espera a la siguiente vuelta, sin reclamarla.</summary>
    SinCupo,

    /// <summary>Otra réplica (u otra vuelta) ya la había reclamado, o la ruta ya no existe: no se hizo nada.</summary>
    NoReclamada,

    /// <summary>La ruta quedó con los camiones del cubicador (o con el respaldo).</summary>
    Cubicada,

    /// <summary>El cubicador no respondió: la ruta sigue pendiente y se reintenta más tarde.</summary>
    Reintento,

    /// <summary>La ruta quedó <c>fallida</c> (rechazo, respuesta inválida o intentos agotados): espera que el comercial la reintente.</summary>
    Fallida,
}

/// <summary>
/// HU-023.5 (#160): cubicación de la licitación en segundo plano. <c>POST /web/licitaciones</c> guarda las rutas con un camión provisional
/// y las deja <c>pendiente</c>; el procesador (<c>clsWorkerCubicacionLicitacion</c>) lee la cola con <see cref="PendientesAsync"/> y
/// procesa cada ruta con <see cref="CubicarAsync"/> en su propio ámbito de DI, con un cupo del cubicador por llamada
/// (<see cref="IClsCupoCubicador"/>, <c>Cubicador:MaximoParalelo</c> entre todas las réplicas). Cuando la última ruta queda cubicada, la
/// solicitud viaja a Nexo completa (todo o nada hacia Nexo).
/// </summary>
public interface IClsCubicacionLicitacionService
{
    /// <summary>
    /// Rutas pendientes con su turno vencido, hasta <c>Cubicador:LoteRutas</c>; con <paramref name="cotizacionId"/>, solo las de esa
    /// cotización (pruebas o una licitación puntual).
    /// </summary>
    Task<IReadOnlyList<clsRutaPorCubicar>> PendientesAsync(Guid? cotizacionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toma un cupo, reclama la ruta (otra réplica no la repite), le pide los camiones al cubicador y los guarda; si era la última, envía la
    /// solicitud a Nexo. Nunca lanza por el cubicador.
    /// </summary>
    Task<ResultadoCubicacionRuta> CubicarAsync(clsRutaPorCubicar ruta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recuperación: envía a Nexo las licitaciones ya cubicadas que no salieron (el proceso se cortó entre la última ruta y el envío) y que
    /// llevan más de <see cref="clsCotizacionSolicitud.EsperaReenvioPendiente"/> sin cambios (con <paramref name="cotizacionId"/>, solo esa).
    /// Devuelve cuántas envió.
    /// </summary>
    Task<int> EnviarCubicadasAsync(Guid? cotizacionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /web/licitaciones/{folio}/cubicacion/reintentar</c>: las rutas fallidas vuelven a la cola o, con
    /// <paramref name="usarRespaldo"/>, las que fallaron por una caída del cubicador (no los rechazos de la petición) se aceptan con el
    /// camión sin vehículo (lo resuelve la Mesa de Costeo) y, si ya no queda nada pendiente, la solicitud sale a Nexo. <c>COTIZACION_NO_ENCONTRADA</c> si no es una licitación suya; <c>ESTADO_NO_PERMITE</c> si no
    /// tiene rutas fallidas.
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> ReintentarAsync(Guid usuarioId, string folio, bool usarRespaldo, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsCubicacionLicitacionService"/>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Réplicas:</b> la ruta se reclama con un <c>UPDATE</c> condicionado al <c>cubicacionDisponibleAt</c> leído (reclamo por
/// <c>Cubicador:ReclamoSegundos</c>); si la réplica muere a mitad, otra la retoma cuando vence. El envío a Nexo se reclama con el
/// <c>updatedAt</c> de la cabecera (el mismo reclamo del reintento): de dos réplicas que terminan juntas, solo una envía.</item>
/// <item><b>Reintentos:</b> solo una caída del cubicador (<c>CUBICADOR_NO_DISPONIBLE</c>, sin respaldo) se reintenta, hasta
/// <c>Cubicador:MaximoIntentos</c>, con espera creciente. Un rechazo de la petición, una respuesta inválida o la llave rechazada dejan la
/// ruta <c>fallida</c> de una vez (nunca se tapan con el respaldo, HU-023.1). Con <c>Cubicador:RespaldoSiFalla</c> una caída nunca llega
/// aquí: el planificador ya devuelve el camión sin vehículo.</item>
/// <item><b>Ruta fallida:</b> la licitación queda en <c>pendiente_envio</c> sin salir a Nexo, con el error en la ruta, hasta que el
/// comercial la reintente (<see cref="ReintentarAsync"/>).</item>
/// </list>
/// </remarks>
public sealed class clsCubicacionLicitacionService(
    IClsCotizacionRepository cotizaciones,
    IClsPlanificadorCamiones planificador,
    IClsUnitOfWork unidadTrabajo,
    IClsEnvioCosteoService envio,
    IClsMargenPoliticaRepository politicas,
    IClsCupoCubicador cupos,
    IClsReloj reloj,
    IOptions<clsOpcionesCubicacionLicitacion> opciones,
    ILogger<clsCubicacionLicitacionService> logger,
    IClsAuditoriaService auditoria,
    TimeProvider tiempo,
    IOptions<clsOpcionesCaptura>? opcionesCaptura = null) : IClsCubicacionLicitacionService
{
    /// <summary>Herramienta de la auditoría de cada ruta cubicada en segundo plano (<c>EventosHerramienta.herramienta</c>, E1 de #174).</summary>
    public const string HerramientaCubicarRuta = "cubicar_ruta_licitacion";

    /// <summary>Herramienta de la auditoría del reintento del comercial (<c>POST .../cubicacion/reintentar</c>, E1 de #174).</summary>
    public const string HerramientaReintentar = "reintentar_cubicacion_licitacion";

    public Task<IReadOnlyList<clsRutaPorCubicar>> PendientesAsync(Guid? cotizacionId = null, CancellationToken cancellationToken = default) =>
        cotizaciones.ListarRutasPorCubicarAsync(reloj.AhoraUtc, opciones.Value.LoteRutas, cotizacionId, cancellationToken);

    public async Task<ResultadoCubicacionRuta> CubicarAsync(clsRutaPorCubicar ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var config = opciones.Value;
        var cupo = await cupos.IntentarTomarAsync(config.MaximoParalelo, cancellationToken);
        if (cupo is null)
        {
            return ResultadoCubicacionRuta.SinCupo;
        }

        clsCotizacionSolicitud cotizacion;
        clsLineaSolicitud linea;
        clsDetalleRuta detalle;
        int versionLeida;
        var cronometro = Stopwatch.StartNew();
        await using (cupo)
        {
            if (!await cotizaciones.ReclamarRutaPorCubicarAsync(ruta.RutaId, ruta.DisponibleAt, reloj.AhoraUtc + config.Reclamo, cancellationToken))
            {
                return ResultadoCubicacionRuta.NoReclamada;
            }

            var leida = await cotizaciones.ObtenerPorIdAsync(ruta.CotizacionId, cancellationToken);
            if (leida?.Rutas.FirstOrDefault(r => r.Id == ruta.RutaId) is not { } encontrada)
            {
                return ResultadoCubicacionRuta.NoReclamada;
            }

            cotizacion = leida;
            versionLeida = leida.VersionNegocio;
            linea = encontrada;
            detalle = await CubicarRutaAsync(cotizacion, linea, config, cancellationToken);
        }

        // Fuera del cupo: ni la auditoría ni el envío a Nexo ocupan al cubicador.
        await AuditarRutaAsync(cotizacion, linea, detalle, (int)cronometro.ElapsedMilliseconds);
        if (detalle.Resultado == ResultadoCubicacionRuta.Cubicada)
        {
            await EnviarTrasCubicarAsync(cotizacion, versionLeida, cancellationToken);
        }

        return detalle.Resultado;
    }

    /// <summary>
    /// La ruta ya quedó guardada como cubicada: lo que falle al intentar el envío (la base, el candado, Nexo) no la deja sin contar ni la
    /// devuelve a la cola. La licitación queda cubicada sin enviar y la recuperación (<see cref="EnviarCubicadasAsync"/>) la envía.
    /// </summary>
    private async Task EnviarTrasCubicarAsync(clsCotizacionSolicitud cotizacion, int versionLeida, CancellationToken cancellationToken)
    {
        try
        {
            await EnviarSiListaAsync(cotizacion, esperarCandado: true, versionLeida, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "La licitación {Folio} quedó cubicada pero no se pudo revisar su envío a Nexo; la recuperación la envía.", cotizacion.Folio);
        }
    }

    /// <summary>Qué pasó con una ruta (para la auditoría): el resultado, el intento, el código del error y los camiones (sin montos ni pesos).</summary>
    private sealed record clsDetalleRuta(ResultadoCubicacionRuta Resultado, int Intento, string? Codigo = null, int Camiones = 0, string? ResultadoCubicador = null);

    private async Task<clsDetalleRuta> CubicarRutaAsync(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud linea, clsOpcionesCubicacionLicitacion config, CancellationToken cancellationToken)
    {
        if (!cotizacion.AdmiteCubicacionEnSegundoPlano(linea.Id))
        {
            // La cotización cambió por otro camino (otro estado, ya en Nexo): la ruta no se vuelve a tomar.
            var intentoNoAdmitido = linea.CubicacionIntentos + 1;
            cotizacion.RegistrarFalloCubicacionDeRuta(linea.Id, clsCodigosError.EstadoNoPermite,
                "La licitación ya no espera la cubicación (cambió de estado).", reintentarEn: null);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            return new clsDetalleRuta(ResultadoCubicacionRuta.Fallida, intentoNoAdmitido, clsCodigosError.EstadoNoPermite);
        }

        // Como el camino síncrono: con Captura:AceptarVehiculoCliente (#138) el vehículo del cliente (el del camión provisional) se
        // conserva para el respaldo; si el cubicador recomienda, manda el cubicador. Sin la bandera, el respaldo va sin vehículo.
        var entrada = opcionesCaptura?.Value.AceptarVehiculoCliente == true
            ? linea.ALinea()
            : linea.ALinea() with { TipoVehiculo = null, Carroceria = null };
        var plan = await planificador.PlanificarAsync(cotizacion.UsuarioId ?? Guid.Empty, linea.Id, entrada, cancellationToken);
        var ahora = reloj.AhoraUtc;
        if (plan.EsExitoso)
        {
            var intento = linea.CubicacionIntentos + 1;
            var completada = cotizacion.CompletarCubicacionDeRuta(linea.Id, plan.Valor, ahora);
            if (completada.EsExitoso)
            {
                await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
                var resultadoCubicador = plan.Valor.Cubicacion?.Resultado ?? "respaldo";
                logger.LogInformation("Ruta {Ruta} de la licitación {Folio} cubicada en segundo plano: {Camiones} camión(es), resultado {Resultado}.",
                    linea.Orden, cotizacion.Folio, plan.Valor.Camiones.Count, resultadoCubicador);
                return new clsDetalleRuta(ResultadoCubicacionRuta.Cubicada, intento, Camiones: plan.Valor.Camiones.Count, ResultadoCubicador: resultadoCubicador);
            }

            return await FallarAsync(cotizacion, linea, completada.Error, reintentar: false, config, ahora);
        }

        return await FallarAsync(cotizacion, linea, plan.Error, plan.Error.Codigo == clsCodigosError.CubicadorNoDisponible, config, ahora);
    }

    private async Task<clsDetalleRuta> FallarAsync(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud linea, clsErrorDominio error, bool reintentar, clsOpcionesCubicacionLicitacion config,
        DateTimeOffset ahora)
    {
        var intento = linea.CubicacionIntentos + 1;
        DateTimeOffset? siguiente = reintentar && intento < config.MaximoIntentos ? ahora + config.EsperaReintento(intento) : null;
        cotizacion.RegistrarFalloCubicacionDeRuta(linea.Id, error.Codigo, error.Mensaje, siguiente);
        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        if (siguiente is null)
        {
            logger.LogWarning("La ruta {Ruta} de la licitación {Folio} no se pudo cubicar ({Codigo}, intento {Intento}): queda fallida y la licitación no sale a Nexo hasta reintentarla.",
                linea.Orden, cotizacion.Folio, error.Codigo, intento);
            return new clsDetalleRuta(ResultadoCubicacionRuta.Fallida, intento, error.Codigo);
        }

        logger.LogInformation("El cubicador no respondió para la ruta {Ruta} de la licitación {Folio} (intento {Intento}): se reintenta a las {Siguiente:o}.",
            linea.Orden, cotizacion.Folio, intento, siguiente);
        return new clsDetalleRuta(ResultadoCubicacionRuta.Reintento, intento, error.Codigo);
    }

    /// <summary>
    /// Desviación E1 (#174): cada ruta procesada en segundo plano deja un evento en <c>EventosHerramienta</c> (<see cref="HerramientaCubicarRuta"/>)
    /// con el folio, la ruta, el intento, el resultado, el código del error y cuántos camiones quedaron. Sin montos, pesos ni mercancías.
    /// </summary>
    private Task AuditarRutaAsync(clsCotizacionSolicitud cotizacion, clsLineaSolicitud linea, clsDetalleRuta detalle, int duracionMs) =>
        auditoria.RegistrarAsync(
            HerramientaCubicarRuta,
            new { folio = cotizacion.Folio, ruta_orden = linea.Orden, linea_solicitud_id = linea.Id, intento = detalle.Intento },
            new
            {
                resultado = detalle.Resultado switch
                {
                    ResultadoCubicacionRuta.Cubicada => "cubicada",
                    ResultadoCubicacionRuta.Reintento => "reintento",
                    _ => "fallida",
                },
                camiones = detalle.Resultado == ResultadoCubicacionRuta.Cubicada ? detalle.Camiones : (int?)null,
                resultado_cubicador = detalle.ResultadoCubicador,
                codigo_error = detalle.Codigo,
            },
            detalle.Resultado == ResultadoCubicacionRuta.Cubicada,
            duracionMs,
            cotizacion.ThreadId);

    public async Task<int> EnviarCubicadasAsync(Guid? cotizacionId = null, CancellationToken cancellationToken = default)
    {
        var ahora = reloj.AhoraUtc;
        var ids = await cotizaciones.ListarCubicadasSinEnviarAsync(
            ahora - clsCotizacionSolicitud.EsperaReenvioPendiente, opciones.Value.LoteRutas, cotizacionId, cancellationToken);
        var enviadas = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // La recuperación no espera el candado: si alguien lo tiene, está enviando; la vuelta siguiente la vuelve a mirar.
            if (await cotizaciones.ObtenerPorIdAsync(id, cancellationToken) is { } cotizacion && await EnviarSiListaAsync(cotizacion, esperarCandado: false, versionLeida: null, cancellationToken))
            {
                enviadas++;
            }
        }

        return enviadas;
    }

    public async Task<clsResultado<clsCotizacionSolicitud>> ReintentarAsync(
        Guid usuarioId, string folio, bool usarRespaldo, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null || cotizacion.TipoSolicitud != TipoSolicitud.Licitacion)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var cronometro = Stopwatch.StartNew();
        var versionLeida = cotizacion.VersionNegocio;
        var cambiadas = cotizacion.ReintentarCubicacionFallida(usarRespaldo);
        if (cambiadas == 0)
        {
            var error = new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                usarRespaldo && cotizacion.Rutas.Any(r => r.EstadoCubicacion == EstadoCubicacion.Fallida)
                    ? $"Las rutas fallidas de la licitación {cotizacion.Folio} las rechazó el cubicador (mercancía o medidas): no se pueden enviar sin vehículo. Reintenta sin usar_respaldo o corrige la carga en una versión nueva."
                    : $"La licitación {cotizacion.Folio} no tiene rutas con la cubicación fallida (o ya salió a Nexo): no hay nada que reintentar.");
            await AuditarReintentoAsync(cotizacion, usarRespaldo, new { error = new { codigo = error.Codigo, mensaje = error.Mensaje } }, exito: false, cronometro);
            return error;
        }

        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        logger.LogInformation("La licitación {Folio}: {Rutas} ruta(s) fallida(s) {Accion}.",
            cotizacion.Folio, cambiadas, usarRespaldo ? "aceptadas con el camión sin vehículo" : "vuelven a la cola del cubicador");
        var enviada = usarRespaldo && await EnviarSiListaAsync(cotizacion, esperarCandado: true, versionLeida, cancellationToken);
        await AuditarReintentoAsync(cotizacion, usarRespaldo, new { rutas = cambiadas, enviada_a_nexo = enviada }, exito: true, cronometro);
        return cotizacion;
    }

    private Task AuditarReintentoAsync(clsCotizacionSolicitud cotizacion, bool usarRespaldo, object salida, bool exito, Stopwatch cronometro) =>
        auditoria.RegistrarAsync(
            HerramientaReintentar,
            new { folio = cotizacion.Folio, usar_respaldo = usarRespaldo },
            salida,
            exito,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);

    /// <summary>
    /// Si la cubicación terminó (todas las rutas cubicadas) y la solicitud no ha salido, con el candado del envío
    /// (<see cref="IClsCupoCubicador.IntentarTomarEnvioAsync"/>) la reclama con el <c>updatedAt</c> de la cabecera y la envía a Nexo con la política de su primera ruta, como el reintento. Sin el token de la
    /// petición: una vez reclamada, el envío se completa (HU-005.5).
    /// </summary>
    private async Task<bool> EnviarSiListaAsync(clsCotizacionSolicitud cotizacion, bool esperarCandado, int? versionLeida, CancellationToken cancellationToken)
    {
        // El reclamo por updatedAt no basta: una réplica que recarga DESPUÉS del reclamo de otra (y antes de que esta guarde el envío)
        // ve la licitación todavía en pendiente_envio y la reclamaría otra vez. El candado del envío cubre la revisión, el reclamo y el
        // envío.
        // B2 (#174): quien no obtiene el candado NO se va. Si las dos últimas rutas terminan juntas, la primera puede tomar el candado,
        // ver a la otra todavía pendiente y soltarlo sin enviar; la última, que ya guardó su ruta, debe volver a revisar con el candado.
        // Por eso espera y reintenta: cuando lo obtiene, la revisión ve todo lo guardado antes de soltarlo. Envío único: el candado, el
        // estado (ListaParaEnviarTrasCubicar) y el reclamo por updatedAt. La espera tiene tope: si otro tiene el candado tanto tiempo, es
        // que está enviando (o la recuperación lo retoma).
        for (var intento = 1; ; intento++)
        {
            var candado = await IntentarTomarCandadoEnvioAsync(cotizacion, intento);
            if (candado is not null)
            {
                await using (candado)
                {
                    return await EnviarConCandadoAsync(cotizacion, versionLeida);
                }
            }

            if (!esperarCandado)
            {
                return false;
            }

            if (intento >= IntentosCandadoEnvio)
            {
                logger.LogInformation("La licitación {Folio}: el candado del envío sigue tomado tras {Intentos} intentos; lo tiene quien la está enviando (o la recuperación la retoma).",
                    cotizacion.Folio, intento);
                return false;
            }

            // La espera usa el reloj inyectado (pruebas con reloj falso) y se corta con el token (apagado o petición cancelada): sin el
            // candado no se reclamó nada, así que dejarlo es seguro y la recuperación toma la licitación.
            try
            {
                await Task.Delay(EsperaCandadoEnvio(intento), tiempo, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("La licitación {Folio}: se canceló la espera del candado del envío; la recuperación la retoma.", cotizacion.Folio);
                return false;
            }
        }
    }

    /// <summary>
    /// Un intento de tomar el candado del envío. Revisión de #174: cada intento abre una conexión propia sin pool (el candado es de
    /// sesión) y, con la base cargada, abrirla puede fallar (<c>Connection Timeout Expired</c> en el saludo previo al login). Antes esa
    /// excepción salía de <see cref="CubicarAsync"/> con la ruta ya guardada: el procesador no la contaba y la licitación esperaba la
    /// recuperación. Ahora un fallo al pedir el candado cuenta como "no obtenido" y se reintenta dentro del mismo tope.
    /// </summary>
    private async Task<IAsyncDisposable?> IntentarTomarCandadoEnvioAsync(clsCotizacionSolicitud cotizacion, int intento)
    {
        try
        {
            return await cupos.IntentarTomarEnvioAsync(cotizacion.Id, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "La licitación {Folio}: no se pudo pedir el candado del envío (intento {Intento}); se reintenta.", cotizacion.Folio, intento);
            return null;
        }
    }

    /// <summary>Intentos de tomar el candado del envío antes de dejarlo a quien lo tiene (B2, #174): 15,75 s de espera en total con <see cref="EsperaCandadoEnvio"/>.</summary>
    internal const int IntentosCandadoEnvio = 35;

    /// <summary>Espera antes del intento <paramref name="intento"/> + 1 del candado del envío: 50 ms que se duplican hasta 500 ms.</summary>
    internal static TimeSpan EsperaCandadoEnvio(int intento) => TimeSpan.FromMilliseconds(Math.Min(500, 50 * Math.Pow(2, Math.Max(0, intento - 1))));

    /// <summary>
    /// Con el candado: recarga, revisa y, si sigue lista, reclama y envía. <paramref name="versionLeida"/> es la <c>versionNegocio</c> que
    /// vio quien cubicó la ruta (o reintentó) antes de guardar; <c>null</c> en la recuperación.
    /// </summary>
    /// <remarks>
    /// Revisión de #174 (doble envío medido bajo carga): el reclamo sube <c>versionNegocio</c>. Si quien envió obtuvo el alta en Nexo pero
    /// no pudo guardar el <c>solicitudNexoId</c> (la base no respondió), la licitación sigue en <c>pendiente_envio</c> y suelta el candado:
    /// la ruta que esperaba lo tomaba, la veía "lista" y la enviaba otra vez (dos altas en Nexo). Con la versión cambiada desde que se
    /// leyó, alguien ya la reclamó: el envío está en curso o es incierto y no se repite aquí; la recuperación aplica la regla de siempre
    /// (<see cref="clsCotizacionSolicitud.EsperaReenvioPendiente"/>), igual que un reenvío cortado a medias (HU-005.5).
    /// </remarks>
    private async Task<bool> EnviarConCandadoAsync(clsCotizacionSolicitud cotizacion, int? versionLeida)
    {
        await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
        if (!cotizacion.ListaParaEnviarTrasCubicar || cotizacion.UsuarioId is not { } usuarioId)
        {
            return false;
        }

        if (versionLeida is { } version && cotizacion.VersionNegocio != version)
        {
            logger.LogWarning("La licitación {Folio} ya fue reclamada para el envío por otro proceso (versión {Leida} → {Actual}) y sigue sin solicitud de Nexo: el envío está en curso o es incierto y no se repite aquí; la recuperación la revisa.",
                cotizacion.Folio, version, cotizacion.VersionNegocio);
            return false;
        }

        if (!await cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, reloj.AhoraUtc, CancellationToken.None))
        {
            return false;
        }

        await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
        var politica = cotizacion.PrimeraRuta?.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, CancellationToken.None) : null;
        logger.LogInformation("Licitación {Folio} cubicada por completo ({Rutas} rutas): se envía a Nexo.", cotizacion.Folio, cotizacion.Rutas.Count);
        await envio.EnviarGuardadaAsync(cotizacion, politica, usuarioId);
        return true;
    }
}
