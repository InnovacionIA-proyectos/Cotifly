using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-036.2 (#206): finalizar la cotización (<c>POST /web/cotizaciones/{folio}/finalizar</c>, con <c>If-Match</c>). El comercial acepta el
/// precio y desde ahí queda congelado (ADR-015 §5, HU-036).
/// </summary>
public interface IClsFinalizacionCotizacionService
{
    /// <summary>
    /// La cotización finalizada (o la misma sin cambios si ya lo estaba: idempotente), o el error: <c>COTIZACION_NO_ENCONTRADA</c> (no
    /// existe o no es del comercial), <c>CONFLICTO_CONCURRENCIA</c> (el <c>If-Match</c> no es la versión vigente, también si ya estaba
    /// finalizada) o <c>COTIZACION_NO_FINALIZABLE</c> (<c>estado_costeo</c> distinto de <c>autocosteo</c> y <c>entregada</c>, conexos de Nexo
    /// pendientes de leer o, #228, conexos que todavía no están en Nexo: <c>detalle.motivo = conexos_pendientes_en_nexo</c>).
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsFinalizacionCotizacionService"/>.</summary>
/// <remarks>
/// <para>El orden de las validaciones es el de las demás mutaciones de la etapa final: primero el dueño (404), después la versión del
/// <c>If-Match</c> (409 <c>CONFLICTO_CONCURRENCIA</c>, como en el resultado) y por último la regla del dominio
/// (<see cref="clsCotizacionSolicitud.Finalizar"/>).</para>
/// <para><b>Concurrencia.</b> La escritura se confirma con la versión del <c>If-Match</c> sobre la fila bloqueada
/// (<see cref="IClsUnitOfWork.GuardarCambiosSiVersionAsync"/>) y sube la versión de negocio. Si el polling cambió el precio entre la lectura
/// del comercial y su clic, la versión ya no coincide: 409 y el comercial recarga (nunca finaliza un precio que no vio). Si la finalización
/// gana, la vuelta del polling que se cruzó no guarda (conflicto) y la siguiente ve la cotización finalizada (precio congelado, #208).</para>
/// <para>Cada finalización que cambia algo, o que se rechaza, queda en <c>EventosHerramienta</c> (<see cref="HerramientaAuditoria"/>,
/// sin montos). Re-finalizar no audita (no cambia nada).</para>
/// </remarks>
public sealed class clsFinalizacionCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    IClsEnvioConexosNexoService envioConexos,
    IClsServiciosConexosNexo nexo,
    ILogger<clsFinalizacionCotizacionService> logger,
    IOptions<clsOpcionesCosteoNexo>? opcionesCosteo = null) : IClsFinalizacionCotizacionService
{
    /// <summary>Herramienta de la auditoría de la finalización (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "finalizar_cotizacion";

    public async Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var cronometro = Stopwatch.StartNew();
        var (resultado, cambio) = await FinalizarAsync(cotizacion, usuarioId, versionEsperada, cancellationToken);
        if (resultado.EsFallo || cambio)
        {
            await auditoria.RegistrarAsync(
                HerramientaAuditoria,
                new { folio = cotizacion.Folio, version_esperada = versionEsperada },
                resultado.EsExitoso
                    ? (object)new
                    {
                        finalizada_at = cotizacion.FinalizadaAt,
                        estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                        version = cotizacion.VersionNegocio,
                    }
                    : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
                resultado.EsExitoso,
                (int)cronometro.ElapsedMilliseconds,
                cotizacion.ThreadId);
        }

        return resultado;
    }

    private async Task<(clsResultado<clsCotizacionSolicitud> Resultado, bool Cambio)> FinalizarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false);
        }

        var pendientesEnNexo = cotizacion.Finalizada || !cotizacion.AdmiteFinalizar || cotizacion.ConexosPendientesDeLeer
            ? 0
            : await ConexosPendientesEnNexoAsync(cotizacion, usuarioId, cancellationToken);
        var finalizada = cotizacion.Finalizar(usuarioId, reloj.AhoraUtc, pendientesEnNexo);
        if (finalizada.EsFallo)
        {
            return (finalizada.Error, false);
        }

        if (!finalizada.Valor)
        {
            // Ya estaba finalizada: 200 sin cambios (mismo ETag), como re-marcar el mismo resultado.
            return (cotizacion, false);
        }

        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? (cotizacion, true)
            : (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false);
    }

    /// <summary>
    /// #228: conexos de las rutas cotizables (las rechazadas por la Mesa no cuentan: su conexo nunca llegará a Nexo) que todavía no están en
    /// la cotización de Nexo: <c>estado_nexo = pendiente</c> en el registro del envío diferido (<see cref="IClsEnvioConexosNexoService.EstadosAsync"/>,
    /// el mismo que ve el comercial en el costeo). <c>incierto</c> y <c>fallido_definitivo</c> no cuentan: el polling ya no los reintenta y
    /// el costeo los muestra para revisarlos en Nexo. #259: si siguen pendientes, la cotización vuelve a la agenda (<see cref="Reencolar"/>).
    /// <para>Validación #228: con alguno pendiente se RELEE la cotización de Nexo y los que ya muestra quedan <c>enviado</c>
    /// (<see cref="IClsEnvioConexosNexoService.RegistrarConfirmadosAsync"/>) antes de contar otra vez. Un registro que se quedó en
    /// <c>pendiente</c> aunque Nexo ya tenga el conexo (agregado con el POST antes de #228, o si su registro falló) bloquearía para siempre
    /// una entregada, que el polling ya no consulta. Si Nexo no responde, se cuenta lo que dice el registro (falla cerrado).</para>
    /// </summary>
    private async Task<int> ConexosPendientesEnNexoAsync(clsCotizacionSolicitud cotizacion, Guid usuarioId, CancellationToken cancellationToken)
    {
        var pendientes = await ContarPendientesAsync(cotizacion, cancellationToken);
        if (pendientes == 0 || cotizacion.CotizacionNexoId is not { } cotizacionNexoId)
        {
            return pendientes;
        }

        var leida = await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, cancellationToken);
        if (leida.EsFallo)
        {
            logger.LogWarning("No se pudo releer la cotización de Nexo de {Folio} para confirmar {Pendientes} conexos pendientes ({Codigo}).",
                cotizacion.Folio, pendientes, leida.Error.Codigo);
            if (Reencolar(cotizacion))
            {
                await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
            }

            return pendientes;
        }

        await envioConexos.RegistrarConfirmadosAsync(cotizacion, leida.Valor, cancellationToken);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        pendientes = await ContarPendientesAsync(cotizacion, cancellationToken);
        if (pendientes > 0 && Reencolar(cotizacion))
        {
            await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        }

        return pendientes;
    }

    /// <summary>
    /// #259: con conexos todavía pendientes, la cotización vuelve a la agenda del polling para que el envío diferido los procese (los envía o
    /// los deja <c>fallido_definitivo</c>) en segundos; finalizar sigue respondiendo 409 y NUNCA hace el POST de conexos: lo hace el worker
    /// con su reclamo, así que no hay doble envío. Con la misma holgura que el lote de conexos (<c>clsCosteoLineasService</c>): solo se mueve
    /// una cotización fuera de la agenda o agendada más allá de dos veces el reclamo, así nunca se cruza con un reclamo vigente. Repara también
    /// las cotizaciones que ya quedaron atascadas antes de #259. Devuelve si cambió la agenda.
    /// </summary>
    private bool Reencolar(clsCotizacionSolicitud cotizacion)
    {
        var ahora = reloj.AhoraUtc;
        var reclamo = (opcionesCosteo?.Value ?? new clsOpcionesCosteoNexo()).Reclamo;
        if (cotizacion.PerdidaSoloEnCotifly || cotizacion.UsuarioId is null
            || (cotizacion.CosteoSiguienteConsultaAt is { } siguiente && siguiente <= ahora + (2 * reclamo)))
        {
            return false;
        }

        cotizacion.ProgramarRelecturaCosteo(ahora);
        logger.LogInformation("{Folio} tiene conexos pendientes de enviar a Nexo al finalizar: vuelve a la agenda del envío diferido.", cotizacion.Folio);
        return true;
    }

    private async Task<int> ContarPendientesAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        var rutas = cotizacion.RutasCotizables.Select(r => r.Id).ToHashSet();
        var estados = await envioConexos.EstadosAsync(cotizacion, cancellationToken);
        return estados.Count(e => rutas.Contains(e.Key.Ruta) && e.Value == clsEnvioConexosNexoService.VisiblePendiente);
    }
}
