using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Seguros;
using Microsoft.Extensions.Logging;

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
    /// HU-046.3 (#269): también <c>COTIZACION_NO_FINALIZABLE</c> con <c>detalle.motivo = medidas_obligatorias_faltantes</c> si una ruta
    /// cotizable no tiene una medida obligatoria de la política de seguros, salvo con <paramref name="justificacion"/> (que salta SOLO ese
    /// bloqueo y queda auditada); <c>SOLICITUD_INVALIDA</c> (400) si la justificación pasa de
    /// <see cref="clsCotizacionSolicitud.LargoMaximoJustificacion"/> caracteres.
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, string? justificacion, CancellationToken cancellationToken = default);

    /// <summary><see cref="FinalizarAsync(Guid, string, int, string?, CancellationToken)"/> sin justificación.</summary>
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
/// <para>HU-046.3 (#269): la política de seguros se lee de sus tablas en cada finalización (sin caché) y se evalúa sobre las rutas
/// cotizables, después de completar el <c>codigoServicio</c> de los conexos que no lo tienen con el índice del catálogo de Nexo. Si la
/// política no se puede leer, la finalización falla (no se finaliza sin evaluarla). La justificación queda en la entrada de la auditoría y,
/// si saltó el bloqueo, las medidas que faltaban en la salida (códigos, sin montos).</para>
/// </remarks>
public sealed class clsFinalizacionCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    IClsEnvioConexosNexoService envioConexos,
    IClsServiciosConexosNexo nexo,
    ILogger<clsFinalizacionCotizacionService> logger,
    IClsPoliticaSegurosRepository? politicaSeguros = null,
    IClsCategoriasConexos? categoriasConexos = null) : IClsFinalizacionCotizacionService
{
    /// <summary>Herramienta de la auditoría de la finalización (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "finalizar_cotizacion";

    public Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, CancellationToken cancellationToken = default) =>
        FinalizarAsync(usuarioId, folio, versionEsperada, null, cancellationToken);

    public async Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, string? justificacion, CancellationToken cancellationToken = default)
    {
        // HU-046.3 (#269): una justificación vacía es como no mandarla; una que no cabe es un cuerpo inválido.
        var motivo = string.IsNullOrWhiteSpace(justificacion) ? null : justificacion.Trim();
        if (motivo is { Length: > clsCotizacionSolicitud.LargoMaximoJustificacion })
        {
            return new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                $"La justificación admite hasta {clsCotizacionSolicitud.LargoMaximoJustificacion} caracteres.");
        }

        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var cronometro = Stopwatch.StartNew();
        var (resultado, cambio, omitidas) = await FinalizarAsync(cotizacion, usuarioId, versionEsperada, motivo, cancellationToken);
        if (resultado.EsFallo || cambio)
        {
            await auditoria.RegistrarAsync(
                HerramientaAuditoria,
                new { folio = cotizacion.Folio, version_esperada = versionEsperada, justificacion = motivo },
                resultado.EsExitoso
                    ? (object)new
                    {
                        finalizada_at = cotizacion.FinalizadaAt,
                        estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                        version = cotizacion.VersionNegocio,
                        // HU-046.3 (#269): las medidas obligatorias que faltaban y que la justificación dejó pasar (vacío si no faltaba ninguna).
                        justificacion_aplicada = omitidas.Count > 0,
                        medidas_omitidas = omitidas.Select(r => new { linea_orden = r.LineaOrden, medidas = r.Medidas.Select(m => m.Codigo).ToList() }).ToList(),
                    }
                    : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
                resultado.EsExitoso,
                (int)cronometro.ElapsedMilliseconds,
                cotizacion.ThreadId);
        }

        return resultado;
    }

    private async Task<(clsResultado<clsCotizacionSolicitud> Resultado, bool Cambio, IReadOnlyList<clsMedidasFaltantesRuta> Omitidas)> FinalizarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int versionEsperada, string? justificacion, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false, []);
        }

        var evaluable = !cotizacion.Finalizada && cotizacion.AdmiteFinalizar && !cotizacion.ConexosPendientesDeLeer;
        var pendientesEnNexo = evaluable ? await ConexosPendientesEnNexoAsync(cotizacion, usuarioId, cancellationToken) : 0;
        // HU-046.3 (#269): la política de seguros de hoy (decisión 7: una finalizada no se recalcula; re-finalizar no la mira).
        var medidas = evaluable ? await MedidasPendientesAsync(cotizacion, cancellationToken) : [];
        var finalizada = cotizacion.Finalizar(usuarioId, reloj.AhoraUtc, pendientesEnNexo, medidas, justificacion);
        if (finalizada.EsFallo)
        {
            return (finalizada.Error, false, []);
        }

        if (!finalizada.Valor)
        {
            // Ya estaba finalizada: 200 sin cambios (mismo ETag), como re-marcar el mismo resultado.
            return (cotizacion, false, []);
        }

        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? (cotizacion, true, medidas)
            : (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false, []);
    }

    /// <summary>
    /// HU-046.3 (#269): las medidas obligatorias pendientes de las rutas cotizables con la política leída AHORA de sus tablas. Antes completa
    /// en memoria el <c>codigoServicio</c> de los conexos que no lo tienen (se guarda con la finalización). Sin repositorio (pruebas que no
    /// lo registran) no exige nada. Si la lectura de la política lanza, la excepción sube: no se finaliza sin evaluarla.
    /// </summary>
    private async Task<IReadOnlyList<clsMedidasFaltantesRuta>> MedidasPendientesAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (politicaSeguros is null)
        {
            return [];
        }

        await clsPoliticaSegurosCosteo.CompletarCodigosAsync(categoriasConexos, cotizacion, logger, cancellationToken);
        var politica = await politicaSeguros.ObtenerAsync(cancellationToken);
        return cotizacion.MedidasObligatoriasPendientes(politica);
    }

    /// <summary>
    /// #228: conexos de las rutas cotizables (las rechazadas por la Mesa no cuentan: su conexo nunca llegará a Nexo) que todavía no están en
    /// la cotización de Nexo: <c>estado_nexo = pendiente</c> en el registro del envío diferido (<see cref="IClsEnvioConexosNexoService.EstadosAsync"/>,
    /// el mismo que ve el comercial en el costeo). <c>incierto</c> y <c>fallido_definitivo</c> no cuentan: el polling ya no los reintenta y
    /// el costeo los muestra para revisarlos en Nexo.
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
            return pendientes;
        }

        await envioConexos.RegistrarConfirmadosAsync(cotizacion, leida.Valor, cancellationToken);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        return await ContarPendientesAsync(cotizacion, cancellationToken);
    }

    private async Task<int> ContarPendientesAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        var rutas = cotizacion.RutasCotizables.Select(r => r.Id).ToHashSet();
        var estados = await envioConexos.EstadosAsync(cotizacion, cancellationToken);
        return estados.Count(e => rutas.Contains(e.Key.Ruta) && e.Value == clsEnvioConexosNexoService.VisiblePendiente);
    }
}
