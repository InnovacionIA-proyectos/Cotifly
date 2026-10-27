using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Options;

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
    /// Nexo), <c>RESULTADO_YA_DEFINIDO</c>, <c>COTIZACION_NO_ENTREGADA</c> y (HU-036.3, #207) <c>COTIZACION_NO_FINALIZADA</c> (409) y
    /// <c>COSTEO_NEXO_NO_DISPONIBLE</c>/<c>COSTEO_NEXO_TIEMPO_AGOTADO</c> (503, sin cambio local). Si el costeo no estaba aplicado,
    /// lo relee y aplica el precio en la misma escritura del resultado (HU-013.14, ADR-018 §17). Sin <c>If-Match</c>.
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="MarcarAsync(Guid, string, string?, string?, string?, CancellationToken)"/> con la versión del
    /// <c>If-Match</c> (<paramref name="versionEsperada"/>, <c>null</c> si no vino): si no coincide, <c>CONFLICTO_CONCURRENCIA</c>.
    /// La perdida solo en Cotifly (HU-013.17: <c>pendiente_costeo</c> sin costeo en Nexo o <c>error_envio</c>) la exige
    /// (<c>PRECONDICION_REQUERIDA</c>).
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, int? versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsResultadoCotizacionService"/>.</summary>
/// <remarks>
/// <para><b><c>comentario</c></b> se reenvía a Nexo tal cual y no se persiste en Cotifly (decisión de la HU-005.14); solo
/// <c>motivo</c> queda en <c>motivoRechazo</c> en una perdida (en una ganada queda <c>"No aplica"</c>, HU-021.13). Límites de Nexo:
/// <c>motivo</c> 100 y <c>comentario</c> 500 caracteres. El límite del motivo solo se valida en una perdida: en una ganada un
/// motivo más largo no se rechaza ni se envía a Nexo (no se guarda en ningún lado).</para>
/// <para><b>Motivo de pérdida de la lista (HU-018.1, #155).</b> En una perdida el <c>motivo</c> es obligatorio y de la lista configurada
/// (<see cref="IClsCatalogoMotivosPerdida"/>, la de Nexo), y <c>Otro</c> exige <c>comentario</c>: si no, <c>MOTIVO_NO_PERMITIDO</c>
/// (400). A Nexo va el motivo con la escritura de la lista y el comentario aparte (su contrato no cambia); con <c>Otro</c>, Cotifly guarda
/// <c>"Otro: comentario"</c> en <c>motivoRechazo</c> para no perder la explicación. Los resultados guardados antes no se revalidan.</para>
/// <para><b>Entrega implícita.</b> Si Nexo entrega la cotización al marcar el resultado (lo hacía antes de su issue #171), la
/// respuesta trae <c>Entregada</c> y <c>estado_costeo</c> salta a <c>entregada</c> aunque estuviera en <c>autocosteo</c> o
/// <c>pendiente_costeo</c>: es un estado válido, no una inconsistencia.</para>
/// <para><b>Costeo no aplicado (HU-013.14, ADR-018 §17).</b> Si la cotización no tiene precio cuando Nexo acepta el resultado, se
/// aplica el costeo de Nexo con la regla del polling en la MISMA escritura del resultado (con <c>versionNegocio</c>). La foto sale, en
/// este orden, de la lectura previa de <c>pendiente_costeo</c>, de la cotización que devuelve Nexo al marcar (si ya están asociadas
/// todas las líneas) o de una relectura. Si no hay foto, el resultado se registra igual, la cotización conserva su estado de costeo y
/// el polling aplica el precio.</para>
/// <para><b>Resultado ya definido en Nexo.</b> Si Nexo responde <c>RESULTADO_YA_DEFINIDO</c> (otro resultado quedó en Nexo y no en
/// Cotifly, p. ej. porque falló el guardado local), Cotifly LEE el resultado de Nexo (<see cref="IClsCosteoNexo.LeerCotizacionAsync"/>, sin
/// escribir: DEF-C-1), lo guarda y responde el 409 con <c>detalle</c> = el resultado de Nexo: Cotifly y Nexo quedan iguales.</para>
/// <para><b>Mismo resultado ya sincronizado (OBS-C-2).</b> Repetir el resultado que Nexo ya tiene responde 200 sin cambios y sin llamar a
/// Nexo.</para>
/// <para><b>Perdida solo en Cotifly (HU-013.17).</b> En <c>error_envio</c>, o en <c>pendiente_costeo</c> cuando Nexo CONFIRMA que no
/// tiene la cotización entregada (si Nexo no responde, 503 sin cambios) o ya no reconoce la solicitud (404), solo se admite <c>perdida</c>
/// con motivo e <c>If-Match</c>: no va a Nexo y detiene el polling.</para>
/// <para><b>Resultado diferido (HU-033, #199, ADR-023, decisión 6).</b> Si la cotización todavía no está Entregada en Nexo, el resultado se
/// guarda SOLO en Cotifly como <c>pendiente</c> de sincronizar y el polling lo envía a Nexo una sola vez cuando ve la entrega
/// (<see cref="IClsSincronizacionResultadoNexoService"/>). Aplica a la ganada en <c>pendiente_costeo</c> (Nexo confirma que no está
/// entregada) y a la ganada o perdida en <c>autocosteo</c> cuando Nexo responde <c>COTIZACION_NO_ENTREGADA</c>. La perdida en
/// <c>pendiente_costeo</c> sigue siendo la "perdida solo en Cotifly" de HU-013.17 (#127: el cliente desistió antes del costeo, el polling
/// se detiene y nunca va a Nexo). Cotifly nunca llama a <c>/entregar</c>. Un segundo <c>/resultado</c> con el mismo valor mientras está
/// pendiente responde 200 sin cambios; con otro valor, <c>RESULTADO_YA_DEFINIDO</c>.</para>
/// </remarks>
public sealed class clsResultadoCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsCosteoNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IClsPrecioCosteoService precioCosteo,
    IOptions<clsOpcionesCosteoNexo> opciones,
    IClsCatalogoMotivosPerdida? motivosPerdida = null) : IClsResultadoCotizacionService
{
    private readonly IClsCatalogoMotivosPerdida _motivosPerdida = motivosPerdida ?? clsCatalogoMotivosPerdida.PorDefecto;

    public const string HerramientaAuditoria = "resultado_cotizacion_nexo";

    /// <summary>Evento de auditoría de la perdida solo en Cotifly (HU-013.17).</summary>
    public const string HerramientaPerdidaSinCosteo = "resultado_cotizacion_sin_costeo";

    /// <summary>HU-033 (#199): evento de auditoría del resultado guardado solo en Cotifly, pendiente de sincronizar con Nexo.</summary>
    public const string HerramientaResultadoPendiente = "resultado_cotizacion_pendiente_nexo";

    public const int LargoMaximoMotivo = 100;
    public const int LargoMaximoComentario = 500;

    public Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, CancellationToken cancellationToken = default) =>
        MarcarAsync(usuarioId, folio, resultado, motivo, comentario, versionEsperada: null, cancellationToken);

    public async Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, int? versionEsperada, CancellationToken cancellationToken = default)
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

        // HU-021.13: en una ganada el motivo no se guarda ("No aplica"), así que su largo no se valida; a Nexo solo va si cabe.
        if (ganada.Value && motivo is { Length: > LargoMaximoMotivo })
        {
            motivo = null;
        }

        if (motivo is { Length: > LargoMaximoMotivo } || comentario is { Length: > LargoMaximoComentario })
        {
            return Invalido($"'motivo' admite máximo {LargoMaximoMotivo} caracteres y 'comentario' {LargoMaximoComentario} (límites de Nexo).");
        }

        // HU-018.1 (#155): en una perdida el motivo es de la lista (la de Nexo) y "Otro" exige comentario. A Nexo va el motivo de la lista
        // y el comentario aparte, como antes; en Cotifly se guarda el motivo (con "Otro", "Otro: comentario"). Lo guardado antes no se revalida.
        var motivoGuardado = motivo;
        if (!ganada.Value)
        {
            var validado = _motivosPerdida.Validar(motivo, comentario);
            if (validado.EsFallo)
            {
                return validado.Error;
            }

            (motivo, comentario, motivoGuardado) = (validado.Valor.Motivo, validado.Valor.Comentario, validado.Valor.ParaCotifly);
        }

        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (versionEsperada is { } esperada && !cotizacion.TieneVersion(esperada))
        {
            return Conflicto(cotizacion);
        }

        if (cotizacion.ResultadoSoloLocal)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} no tiene resultado en Nexo (chat, descartada o costeo vencido): márcalo con el seguimiento de Cotifly.");
        }

        // HU-036.3 (#207): con el costeo listo, el resultado se marca sobre el precio que aceptó el comercial. Antes del costeo siguen la
        // perdida solo en Cotifly (HU-013.17) y la ganada pendiente de sincronizar (HU-033).
        if (cotizacion.ValidarFinalizadaAntesDelResultado() is { } sinFinalizar)
        {
            return sinFinalizar;
        }

        // HU-033 (#199): ya hay un resultado esperando la entrega de Nexo. El mismo valor es idempotente (200 sin cambios); otro valor no.
        if (cotizacion.ResultadoPendienteDeSincronizar)
        {
            return cotizacion.Ganada == ganada.Value
                ? cotizacion
                : new clsErrorDominio(clsCodigosError.ResultadoYaDefinido,
                    $"La cotización {cotizacion.Folio} ya tiene resultado registrado (pendiente de sincronizar con Nexo): crea una versión nueva para cambiarlo.",
                    cotizacion.Ganada == true ? "ganada" : "perdida");
        }

        // OBS-C-2 (E2E 2026-10-22): el mismo resultado que ya está sincronizado con Nexo es idempotente en Cotifly: 200 sin cambios y sin
        // volver a escribir en Nexo (antes repetía POST /api/cotizaciones/{id}/resultado). Otro valor sigue yendo a Nexo, que manda.
        if (cotizacion.Ganada == ganada.Value
            && cotizacion.EstadoSincronizacionResultado == clsCodigoEnum.ACodigo(SincronizacionResultadoNexo.Sincronizado))
        {
            return cotizacion;
        }

        if (cotizacion.Ganada is not null && cotizacion.EstadoCosteo is EstadoCosteo.PendienteCosteo or EstadoCosteo.ErrorEnvio)
        {
            return new clsErrorDominio(clsCodigosError.ResultadoYaDefinido,
                $"La cotización {cotizacion.Folio} ya tiene resultado registrado: crea una versión nueva para cambiarla.");
        }

        // ADR-019 (Riesgo 2): "en Nexo" sin el id de la solicitud (migrada sin respaldo) se trata como un envío fallido: queda en
        // error_envio con SOLICITUD_NEXO_DESCONOCIDA y la perdida va solo en Cotifly (#127); la ganada pide reintentar el envío.
        if (cotizacion.Ganada is null && cotizacion.SolicitudNexoDesconocida)
        {
            cotizacion.MarcarSolicitudNexoDesconocida();
        }

        // HU-013.17: Nexo no recibió la solicitud; si el cliente desistió, la perdida queda solo en Cotifly.
        if (cotizacion.EstadoCosteo == EstadoCosteo.ErrorEnvio)
        {
            return await RegistrarPerdidaSinCosteoAsync(cotizacion, ganada.Value, motivoGuardado, versionEsperada);
        }

        if (cotizacion.SolicitudNexoId is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no está en Nexo: reintenta el envío antes de marcar el resultado.");
        }

        // Nexo solo acepta el resultado de una cotización entregada: sin costeo completo no se le pregunta (y no se crea en
        // Nexo un borrador vacío solo para que lo rechace). En autocosteo sí se intenta: el polling pudo no ver aún la entrega.
        if (cotizacion.EstadoCosteo is not (EstadoCosteo.Entregada or EstadoCosteo.Autocosteo or EstadoCosteo.PendienteCosteo))
        {
            return NoEntregada(cotizacion);
        }

        // HU-013.14: en pendiente_costeo Cotifly pudo no haber visto una entrega de Nexo. Se lee primero (como el polling) y esa misma
        // foto se aplica después (una sola lectura). Sin la cotización entregada en Nexo no se le pide el resultado: una perdida queda
        // solo en Cotifly (HU-013.17) y una ganada es COTIZACION_NO_ENTREGADA.
        clsCosteoConsultadoNexo? previa = null;
        if (cotizacion.EstadoCosteo == EstadoCosteo.PendienteCosteo)
        {
            var lectura = await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.SolicitudNexoId, cancellationToken);
            if (lectura.EsFallo && lectura.Error.Codigo == clsCodigosError.CosteoNexoSolicitudNoEncontrada && !ganada.Value)
            {
                // E2E 2026-10-22: Nexo CONFIRMA que ya no tiene la solicitud (404): no hay costeo que esperar. La perdida queda solo en
                // Cotifly, como en error_envio (HU-013.17); antes respondía COSTEO_NEXO_SOLICITUD_NO_ENCONTRADA.
                return await RegistrarPerdidaSinCosteoAsync(cotizacion, ganada.Value, motivoGuardado, versionEsperada);
            }

            if (lectura.EsFallo)
            {
                // Sin la respuesta de Nexo no se sabe si ya entregó: nada se registra (ni la perdida solo en Cotifly, que dejaría a
                // Cotifly y Nexo distintos para siempre). Nexo caído o lento es 503 reintentable.
                return lectura.Error.Codigo is clsCodigosError.CosteoNexoNoDisponible or clsCodigosError.CosteoNexoTiempoAgotado || !ganada.Value
                    ? lectura.Error
                    : NoEntregada(cotizacion);
            }

            if (clsDerivacionEstadoCosteo.Derivar(lectura.Valor) != EstadoCosteo.Entregada)
            {
                // Nexo CONFIRMA que no hay entrega: la perdida queda solo en Cotifly (HU-013.17, #127: el cliente desistió antes del costeo);
                // la ganada queda pendiente de sincronizar y va a Nexo cuando la Mesa entregue (HU-033, #199).
                return ganada.Value
                    ? await RegistrarPendienteNexoAsync(cotizacion, ganada.Value, motivoGuardado, comentario, versionEsperada ?? cotizacion.VersionNegocio)
                    : await RegistrarPerdidaSinCosteoAsync(cotizacion, ganada.Value, motivoGuardado, versionEsperada);
            }

            previa = lectura.Valor;
            if (cotizacion.CotizacionNexoId is null && previa.Cotizacion?.Id is { Length: > 0 } idNexo)
            {
                // La lectura ya trae la cotización de Nexo: no hace falta "crear o recuperar" otra vez antes de marcar.
                cotizacion.RegistrarCotizacionNexo(idNexo);
            }
        }

        var cronometro = Stopwatch.StartNew();
        var versionLeida = cotizacion.VersionNegocio;
        var marcada = await MarcarEnNexoAsync(cotizacion, usuarioId, ganada.Value, motivo, comentario, cancellationToken);
        if (marcada.EsFallo && marcada.Error.Codigo == clsCodigosError.ResultadoYaDefinido)
        {
            return await SincronizarResultadoDeNexoAsync(cotizacion, usuarioId, versionLeida, ganada.Value, marcada.Error, cronometro);
        }

        if (marcada.EsFallo && marcada.Error.Codigo == clsCodigosError.CotizacionNoEntregada)
        {
            // HU-033 (#199): Nexo todavía no tiene la cotización entregada (autocosteo): el resultado queda solo en Cotifly, pendiente.
            return await RegistrarPendienteNexoAsync(cotizacion, ganada.Value, motivoGuardado, comentario, versionEsperada ?? versionLeida);
        }

        string? origenFoto = null;
        if (marcada.EsExitoso)
        {
            // Nexo ya tiene el resultado: desde aquí sin el token de la petición (la lectura y la escritura local no se cortan).
            var (foto, origen) = await FotoParaAplicarAsync(cotizacion, usuarioId, previa, marcada.Valor);
            origenFoto = origen;
            await RegistrarResultadoAsync(cotizacion, versionLeida, ganada.Value, motivoGuardado, marcada.Valor, foto);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, resultado = ganada.Value ? "ganada" : "perdida", motivo = motivoGuardado, cotizacion_nexo_id = cotizacion.CotizacionNexoId },
            marcada.EsExitoso
                ? (object)new
                {
                    estado_nexo = marcada.Valor.Estado,
                    resultado_nexo = marcada.Valor.Resultado,
                    costeo = origenFoto,
                    estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                    con_precio = cotizacion.PrecioFinal is not null,
                }
                : new { error = new { codigo = marcada.Error.Codigo, mensaje = marcada.Error.Mensaje } },
            marcada.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return marcada.EsExitoso ? cotizacion : marcada.Error;
    }

    /// <summary>
    /// HU-033 (#199): guarda el resultado solo en Cotifly, pendiente de enviarlo a Nexo cuando la Mesa entregue la cotización (con la
    /// versión de negocio: <c>CONFLICTO_CONCURRENCIA</c> si cambió). Si el polling ya no la consultaba (pasó la edad máxima en
    /// <c>autocosteo</c>), la vuelve a agendar para ver la entrega.
    /// </summary>
    private async Task<clsResultado<clsCotizacionSolicitud>> RegistrarPendienteNexoAsync(
        clsCotizacionSolicitud cotizacion, bool ganada, string? motivo, string? comentario, int version)
    {
        var cronometro = Stopwatch.StartNew();
        var ahora = reloj.AhoraUtc;
        cotizacion.RegistrarResultadoPendienteNexo(ganada, Normalizar(motivo), Normalizar(comentario), ahora);
        if (cotizacion.CosteoSiguienteConsultaAt is null)
        {
            cotizacion.ProgramarRelecturaCosteo(ahora);
        }

        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None))
        {
            return Conflicto(cotizacion);
        }

        await auditoria.RegistrarAsync(
            HerramientaResultadoPendiente,
            new { folio = cotizacion.Folio, resultado = ganada ? "ganada" : "perdida", motivo },
            new
            {
                estado_costeo = cotizacion.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null,
                enviado_a_nexo = false,
                sincronizacion = cotizacion.EstadoSincronizacionResultado,
            },
            exito: true,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return cotizacion;
    }

    /// <summary>
    /// Perdida solo en Cotifly (HU-013.17, ADR-018 §17): solo <c>perdida</c>, con motivo y con <c>If-Match</c>. Detiene el polling, sube
    /// la versión de negocio y queda en la auditoría. Cuenta en las métricas como cualquier perdida (<c>ganada = 0</c>).
    /// </summary>
    private async Task<clsResultado<clsCotizacionSolicitud>> RegistrarPerdidaSinCosteoAsync(clsCotizacionSolicitud cotizacion, bool ganada, string? motivo, int? versionEsperada)
    {
        if (ganada)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEntregada,
                $"La cotización {cotizacion.Folio} no tiene el costeo entregado por Nexo: solo se puede marcar como perdida (el cliente desistió).");
        }

        if (Normalizar(motivo) is not { } motivoLimpio)
        {
            return Invalido("Para marcar perdida una cotización sin costeo de Nexo, el campo 'motivo' es obligatorio.");
        }

        if (versionEsperada is not { } version)
        {
            return new clsErrorDominio(clsCodigosError.PrecondicionRequerida, "Recarga la cotización y vuelve a intentarlo (falta If-Match).");
        }

        var cronometro = Stopwatch.StartNew();
        var estado = cotizacion.EstadoCosteo;
        cotizacion.RegistrarPerdidaSinCosteo(motivoLimpio, reloj.AhoraUtc);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None))
        {
            return Conflicto(cotizacion);
        }

        await auditoria.RegistrarAsync(
            HerramientaPerdidaSinCosteo,
            new { folio = cotizacion.Folio, resultado = "perdida", motivo = motivoLimpio },
            new { estado_costeo = estado is { } e ? clsCodigoEnum.ACodigo(e) : null, enviado_a_nexo = false, polling_detenido = true },
            exito: true,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return cotizacion;
    }

    /// <summary>
    /// Nexo respondió <c>RESULTADO_YA_DEFINIDO</c>: lee el resultado que tiene Nexo y, si es distinto del de Cotifly, lo guarda (con la
    /// versión de negocio). Responde siempre el 409, con <c>detalle</c> = <c>ganada</c>/<c>perdida</c> (el de Nexo).
    /// </summary>
    private async Task<clsResultado<clsCotizacionSolicitud>> SincronizarResultadoDeNexoAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int versionLeida, bool pedido, clsErrorDominio rechazo, Stopwatch cronometro)
    {
        // DEF-C-1 (E2E 2026-10-22): solo LEE (GET por id o por solicitud); antes hacía POST /api/cotizaciones ("crear o recuperar").
        var leida = await nexo.LeerCotizacionAsync(usuarioId, cotizacion.SolicitudNexoId!, cotizacion.CotizacionNexoId, CancellationToken.None);
        bool? deNexo = leida.EsExitoso
            ? leida.Valor.Resultado.Trim().ToLowerInvariant() switch { "ganada" => true, "perdida" => false, _ => null }
            : null;
        var sincronizada = false;
        if (deNexo is { } valor && cotizacion.Ganada != valor)
        {
            await RegistrarResultadoAsync(cotizacion, versionLeida, valor, motivo: null, leida.Valor, foto: null);
            sincronizada = true;
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, resultado = pedido ? "ganada" : "perdida", cotizacion_nexo_id = cotizacion.CotizacionNexoId },
            new
            {
                error = new { codigo = rechazo.Codigo, mensaje = rechazo.Mensaje },
                resultado_nexo = deNexo is { } r ? (r ? "ganada" : "perdida") : null,
                sincronizado_en_cotifly = sincronizada,
            },
            exito: false,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);

        return deNexo is { } definido
            ? new clsErrorDominio(clsCodigosError.ResultadoYaDefinido,
                $"Nexo ya tiene la cotización {cotizacion.Folio} como {(definido ? "ganada" : "perdida")}"
                + (sincronizada ? ": Cotifly quedó con ese mismo resultado." : " y Cotifly también.")
                + " Para cambiarlo crea una versión nueva.",
                definido ? "ganada" : "perdida")
            : rechazo;
    }

    /// <summary>
    /// Foto del costeo para aplicar con el resultado si falta el precio (HU-013.14), sin repetir llamadas a Nexo: la lectura previa,
    /// la cotización entregada que devolvió Nexo al marcar (si todas las líneas ya tienen su id de Nexo) o, por último, una relectura.
    /// <c>null</c> si no hace falta o si la relectura falló.
    /// </summary>
    private async Task<(clsCosteoConsultadoNexo? Foto, string? Origen)> FotoParaAplicarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsCosteoConsultadoNexo? previa, clsCotizacionNexo marcada)
    {
        if (!RequiereLectura(cotizacion))
        {
            return (null, null);
        }

        if (previa is not null)
        {
            return (previa, "lectura_previa");
        }

        if (string.Equals(marcada.Estado, clsCotizacionNexo.EstadoEntregada, StringComparison.OrdinalIgnoreCase) && marcada.Lineas.Count > 0)
        {
            // HU-021: si todos los camiones ya tienen su línea de Nexo, la respuesta del resultado basta como foto.
            // HU-028.1 (#183): los rechazados que quedaron fuera del envío no tienen línea en esta solicitud (no se esperan).
            var camiones = cotizacion.Vehiculos.Where(c => !c.FueraDelEnvio).ToList();
            if (camiones.Count > 0 && camiones.All(c => c.LineaSolicitudNexoId is not null))
            {
                return (new clsCosteoConsultadoNexo(
                    clsCosteoConsultadoNexo.SolicitudEntregada, [.. camiones.Select(c => c.LineaSolicitudNexoId!)], marcada),
                    "respuesta_resultado");
            }
        }

        var lectura = await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.SolicitudNexoId!, CancellationToken.None);
        return lectura.EsExitoso ? (lectura.Valor, "relectura") : (null, "relectura_fallida");
    }

    /// <summary>¿Falta aplicar el costeo? Sin precio y sin PDF (con PDF el precio de la versión ya quedó fijo).</summary>
    private static bool RequiereLectura(clsCotizacionSolicitud cotizacion) => cotizacion.PrecioFinal is null && cotizacion.PdfPath is null;

    /// <summary>
    /// Guarda el resultado que Nexo ya aceptó subiendo la versión de negocio, con <c>GuardarCambiosSiVersionAsync</c> sobre la versión
    /// leída (ADR-018 §12: bloquea primero la cotización, como los demás flujos). Nexo ya lo tiene, así que si otra escritura ganó se
    /// recarga y se vuelve a aplicar (hasta 3 veces); desde ese momento un ajuste responde <c>PDF_YA_ENVIADO</c>. Si el costeo no
    /// estaba aplicado (HU-013.14) se aplica <paramref name="foto"/> en la misma escritura; sin foto se agenda al polling sin tocar el
    /// estado de costeo. Si el polling ganó la carrera y ya puso el precio, la recarga lo trae y no se reaplica. Si los 3 intentos
    /// fallan, un nuevo intento del comercial lo repara: Nexo responde el mismo resultado (idempotente) o <c>RESULTADO_YA_DEFINIDO</c>,
    /// que se sincroniza.
    /// </summary>
    private async Task RegistrarResultadoAsync(
        clsCotizacionSolicitud cotizacion, int versionLeida, bool ganada, string? motivo, clsCotizacionNexo nexoMarcada, clsCosteoConsultadoNexo? foto)
    {
        var version = versionLeida;
        for (var intento = 0; intento < 3; intento++)
        {
            if (intento > 0)
            {
                await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
                version = cotizacion.VersionNegocio;
            }

            var ahora = reloj.AhoraUtc;
            cotizacion.MarcarSeguimiento(ganada, motivo, ahora);
            // HU-033 (#199): Nexo ya tiene el resultado.
            cotizacion.MarcarResultadoSincronizado(ahora);
            if (RequiereLectura(cotizacion))
            {
                if (foto is not null)
                {
                    // Misma regla que el polling: estado, agenda y precio (por camión y por ruta, ADR-017, HU-021) con el margen guardado.
                    var pendientes = cotizacion.ConexosPendientesDeLeer
                        ? await cotizaciones.ObtenerConexosPendientesNexoAsync(cotizacion.Folio, CancellationToken.None)
                        : null;
                    clsLecturaCosteo.AplicarFoto(cotizacion, foto, ahora, opciones.Value, precioCosteo, pendientes);
                }
                else
                {
                    // Sin costo leído: el estado de costeo se conserva y el polling aplica el precio (nunca "entregada" huérfana).
                    cotizacion.ProgramarRelecturaCosteo(ahora);
                }
            }
            else if (string.Equals(nexoMarcada.Estado, clsCotizacionNexo.EstadoEntregada, StringComparison.OrdinalIgnoreCase)
                && cotizacion.EstadoCosteo != EstadoCosteo.Entregada)
            {
                // Entrega implícita (o entrega que el polling aún no vio) con el precio ya aplicado: válida, no una inconsistencia.
                cotizacion.RegistrarEntregaImplicita(nexoMarcada.Id);
            }

            cotizacion.IncrementarVersionNegocio();
            if (await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None))
            {
                return;
            }
        }

        throw new InvalidOperationException($"No se pudo registrar el resultado de la cotización {cotizacion.Folio} después de 3 intentos (Nexo ya lo tiene).");
    }

    private async Task<clsResultado<clsCotizacionNexo>> MarcarEnNexoAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, bool ganada, string? motivo, string? comentario, CancellationToken cancellationToken)
    {
        var cotizacionNexoId = cotizacion.CotizacionNexoId;
        if (cotizacionNexoId is null)
        {
            // El polling todavía no la vio: se LEE la cotización de Nexo de la solicitud (DEF-C-1: sin "crear o recuperar"). Si Nexo aún no
            // la tiene, COTIZACION_NO_ENTREGADA (el resultado queda pendiente en autocosteo, HU-033).
            var recuperada = await nexo.LeerCotizacionAsync(usuarioId, cotizacion.SolicitudNexoId!, cotizacionNexoId: null, cancellationToken);
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

    private static clsErrorDominio NoEntregada(clsCotizacionSolicitud cotizacion) => new(clsCodigosError.CotizacionNoEntregada,
        $"La cotización {cotizacion.Folio} todavía no tiene el costeo entregado por Nexo: no se puede marcar el resultado.");

    private static clsErrorDominio Conflicto(clsCotizacionSolicitud cotizacion) => new(clsCodigosError.ConflictoConcurrencia,
        $"La cotización {cotizacion.Folio} cambió desde que la leíste: recárgala y vuelve a intentarlo.");

    private static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static clsErrorDominio Invalido(string mensaje) => new(clsSeguimientoService.CodigoSeguimientoInvalido, mensaje);
}
